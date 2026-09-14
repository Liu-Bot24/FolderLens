using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using FolderLens.Contracts;
using FolderLens.Core;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record ImageReply(WorkerEnvelope Message,string? AssetPath);

/// <summary>One persistent foreground worker, one active request. Cancelling invalidates and terminates that instance.</summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private readonly string executable,tempRoot;
    private readonly WorkerPriority priority;
    private readonly WorkerResources resources;
    public WorkerClient(string executable,string tempRoot,WorkerPriority priority=WorkerPriority.Foreground):this(executable,tempRoot,priority,WorkerResources.Shared){}
    internal WorkerClient(string executable,string tempRoot,WorkerPriority priority,WorkerResources resources)
    {this.executable=executable;this.tempRoot=tempRoot;this.priority=priority;this.resources=resources;resources.MemoryPressure+=OnMemoryPressure;}
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly SemaphoreSlim requestGate=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private Task idleReclaimTask=Task.CompletedTask;
    private int disposing;
    private readonly object disposalSync=new();
    private Task? disposalTask;
    private void OnMemoryPressure()
    {
        // A foreground animation/text session can carry required decoder state.
        // Background workers are reconstructible, but assets still belong to their
        // consumers until ReleaseAsset; never remove those files prematurely.
        if(priority==WorkerPriority.Foreground||Volatile.Read(ref disposing)!=0||!gate.Wait(0))return;
        if(process is null||producedAssets.Count!=0){gate.Release();return;}
        idleReclaimTask=ReclaimIdle();
    }
    private async Task ReclaimIdle()
    {
        try{await Stop().ConfigureAwait(false);}
        finally{gate.Release();}
    }
    private Process? process;
    private NamedPipeServerStream? pipe;
    private WorkerJob? job;
    private string? instance,taskDirectory,currentPath,currentInput;
    private long? currentLength,currentWrite;
    private long currentVersion;
    private readonly Dictionary<string,(string Path,string Instance)> producedAssets=new(StringComparer.Ordinal);
    private string? currentInputFile;
    private readonly SemaphoreSlim identityGate=new(1,1);
    private string? providerIdentity;
    private WorkerTemporaryFiles.Session? temporarySession;
    public WorkerTemporaryCleanup? LastTemporaryCleanup {get;private set;}

    private readonly HashSet<string> requestedOutputs=new(StringComparer.OrdinalIgnoreCase);
    public async Task<string> GetProviderIdentity(CancellationToken cancellation)
    {
        await identityGate.WaitAsync(cancellation).ConfigureAwait(false);
        try{return providerIdentity??=await Task.Run(()=>ComputeProviderIdentity(executable,cancellation),cancellation).ConfigureAwait(false);}
        finally{identityGate.Release();}
    }
    internal static string ComputeProviderIdentity(string executable,CancellationToken cancellation,Action<int>? readObserved=null)
    {
        cancellation.ThrowIfCancellationRequested();using var background=new BackgroundThreadScope();
        string directory=Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var enumeration=new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false,MaxRecursionDepth=8};
        string[] files=Directory.EnumerateFiles(directory,"*",enumeration).Select(p=>{cancellation.ThrowIfCancellationRequested();return p;}).Where(p=>Path.GetExtension(p).Equals(".dll",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(p).Equals("policy.xml",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(p).Equals(".icc",StringComparison.OrdinalIgnoreCase)).Append(Path.GetFullPath(executable)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4097).Order(StringComparer.Ordinal).ToArray();
        if(files.Length>4096)throw new IOException("工作进程组件超过版本校验文件预算。");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[128*1024];
        hash.AppendData(Encoding.UTF8.GetBytes(WorkerProtocol.BuildId));
        foreach(string path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory,path)));hash.AppendData(new byte[]{0});
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,buffer.Length,FileOptions.SequentialScan);hash.AppendData(BitConverter.GetBytes(file.Length));
            while(true){cancellation.ThrowIfCancellationRequested();int read=file.Read(buffer);if(read==0)break;readObserved?.Invoke(read);hash.AppendData(buffer.AsSpan(0,read));}
        }
        return "sha256:"+Convert.ToHexString(hash.GetHashAndReset());
    }
    public async Task ReleaseAsset(ImageReply reply)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(reply.AssetPath is null||reply.Message.RequestId is not {} token)return;
            if(!producedAssets.TryGetValue(token,out var registered))return;
            if(registered.Path!=reply.AssetPath||registered.Instance!=reply.Message.WorkerInstanceId)throw new InvalidDataException("不匹配的工作进程资产释放请求。");
            await Task.Run(()=>{if(File.Exists(registered.Path))ThumbnailCache.DeleteOwned(registered.Path);}).ConfigureAwait(false);
            producedAssets.Remove(token);
            requestedOutputs.Remove(registered.Path);
        }
        finally{gate.Release();}
    }
    public Task<ImageReply> Request(string path,string operation,RequestContext context,ImageParameters parameters,CancellationToken cancellation,SourceFileStamp? sourceStamp=null)=>RequestCore(path,operation,context,parameters,cancellation,sourceStamp,false);
    public async Task<RuntimeCapabilities> GetRuntimeCapabilities(CancellationToken cancellation)
    {
        var reply=await RequestCore(null,"capabilities",new("runtime",1,1,1,1,1),new ImageParameters(),cancellation,null,false,true);
        return reply.Message.Metadata!.Value.GetProperty("capabilities").Deserialize<RuntimeCapabilities>(WorkerProtocol.Json)??throw new InvalidDataException("Invalid capability report.");
    }
    public Task<ImageReply> RenderMarkdown(string path,RequestContext context,CancellationToken cancellation,string? encoding=null,SourceFileStamp? sourceStamp=null)=>RequestCore(path,"markdownRender",context,new{maxInputBytes=8388608,maxAstNodes=100000,maxHtmlBytes=16777216,encoding},cancellation,sourceStamp,false);
    public Task<ImageReply> RequestData(string path,string operation,RequestContext context,object parameters,CancellationToken cancellation,SourceFileStamp? sourceStamp=null)
    {
        if(operation is not("textWindow" or "textFind" or "textLinePosition" or "textIndexStep"))throw new ArgumentException("未知文本工作进程操作。",nameof(operation));
        return RequestCore(path,operation,context,parameters,cancellation,sourceStamp,true);
    }
    private async Task<ImageReply> RequestCore(string? path,string operation,RequestContext context,object parameters,CancellationToken cancellation,SourceFileStamp? sourceStamp,bool dataOnly,bool fileless=false)
    {
        if(!fileless)path=PathRules.ValidateSource(path!);sourceStamp?.Validate();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);cancellation=linked.Token;
        await requestGate.WaitAsync(cancellation).ConfigureAwait(false);
        WorkerResources.Lease? lease=null;CancellationTokenSource? deadline=null;Timer? diskGuard=null;
        int diskFailure=0;bool ownsGate=false;
        try
        {
            lease=await resources.Acquire(priority,cancellation).ConfigureAwait(false);
            await gate.WaitAsync(cancellation).ConfigureAwait(false);ownsGate=true;
            await idleReclaimTask.ConfigureAwait(false);
            deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lease.PressureCancellation);
            double seconds=operation=="markdownRender"?3:operation=="capabilities"||dataOnly?30:priority!=WorkerPriority.Foreground?10:60;
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));var token=deadline.Token;
            long memoryLimit=operation=="markdownRender"||dataOnly?1L<<30:2L<<30;
            memoryLimit=Math.Min(memoryLimit,Math.Max(256L<<20,resources.Snapshot.HardLimitBytes/2));
            if(process is null||process.HasExited)await Start(token,lease.CpuThreads,memoryLimit).ConfigureAwait(false);
            if(producedAssets.Count>=512)throw new WorkerResourceLimitException("待释放的媒体预览过多，请关闭旧预览后重试。");
            diskGuard=new Timer(_=>
            {
                try{temporarySession?.CheckBudget(1L<<30);}
                catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception){Interlocked.Exchange(ref diskFailure,1);try{deadline.Cancel();}catch(ObjectDisposedException){}}
            },null,TimeSpan.FromMilliseconds(250),TimeSpan.FromMilliseconds(250));
            // Only local approval descriptors are read/written by the host. All
            // source stat calls, including unindexed opens, belong to the worker.
            if(!fileless&&(dataOnly||currentPath!=path||currentLength!=sourceStamp?.Length||currentWrite!=sourceStamp?.ModifiedUtcTicks||currentVersion!=context.FileVersion))
            {
                if(currentInputFile is {} previous){await Task.Run(()=>{if(File.Exists(previous))ThumbnailCache.DeleteOwned(previous);},token).ConfigureAwait(false);currentInputFile=null;}
                currentInput=Guid.NewGuid().ToString("N");currentPath=dataOnly?null:path;currentLength=sourceStamp?.Length;currentWrite=sourceStamp?.ModifiedUtcTicks;currentVersion=context.FileVersion;
                currentInputFile=Path.Combine(taskDirectory!,currentInput+".input.json");
                object approved=dataOnly?new{path,expectedLength=sourceStamp?.Length,expectedLastWriteTicks=sourceStamp?.ModifiedUtcTicks}:new ApprovedInput(path!,currentLength,currentWrite);
                try{await File.WriteAllTextAsync(currentInputFile,JsonSerializer.Serialize(approved,WorkerProtocol.Json),token).ConfigureAwait(false);}catch{currentPath=null;throw;}
            }
            string requestId=Guid.NewGuid().ToString("N");
            if(!dataOnly&&!fileless&&operation is not("probe" or "animationClose"))requestedOutputs.Add(Path.Combine(taskDirectory!,requestId+(operation=="markdownRender"?".html":".png")));
            var request=new WorkerEnvelope{WorkerInstanceId=instance!,RequestId=requestId,Context=context,Operation=operation,FileRef=fileless?null:new(context.RootId+":"+Path.GetFileName(path),currentInput!),DeadlineUtc=DateTimeOffset.UtcNow.AddSeconds(seconds),ResourceBudget=new(memoryLimit,1L<<30,lease.CpuThreads),Parameters=JsonSerializer.SerializeToElement(parameters,WorkerProtocol.Json)};
            await WorkerProtocol.Write(pipe!,request,token).ConfigureAwait(false);
            var response=await WorkerProtocol.Read(pipe!,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if(response.Type!="response"||response.WorkerInstanceId!=instance||response.RequestId!=requestId||response.Context!=context)throw new InvalidDataException("Stale or mismatched worker response.");
            if(response.Status!="ok")
            {
                if(response.ErrorCode=="FileChanged")throw new IOException("FileChanged");
                if(response.ErrorCode=="SourceMissing")throw new FileNotFoundException("原文件已不存在或目录暂不可用。",path);
                if(response.ErrorCode is "AccessDenied" or "TextAccessDenied")throw new UnauthorizedAccessException("没有读取原文件的权限。");
                if(response.ErrorCode=="SourceIoError")throw new IOException("原文件读取失败，目录可能已离线。");
                throw new InvalidDataException(response.ErrorCode??"DecodeFailed");
            }
            if((dataOnly||fileless)&&response.AssetToken is not null)throw new InvalidDataException("数据响应不得包含文件资产。");
            string? asset=null;
            if(response.AssetToken is {} assetToken)
            {
                if(!WorkerProtocol.SafeToken(assetToken)||assetToken!=requestId)throw new InvalidDataException("Invalid asset token.");
                asset=Path.Combine(taskDirectory!,assetToken+(operation=="markdownRender"?".html":".png"));using(var verified=ThumbnailCache.OpenOwnedRead(asset)){}producedAssets.Add(assetToken,(asset,instance!));
            }
            token.ThrowIfCancellationRequested();return new(response,asset);
        }
        catch(OperationCanceledException)
        {
            if(!ownsGate){await gate.WaitAsync().ConfigureAwait(false);ownsGate=true;}
            await Stop().ConfigureAwait(false);
            if(cancellation.IsCancellationRequested)throw;
            if(diskFailure!=0)throw new WorkerResourceLimitException("工作进程临时磁盘预算已触发，任务已停止。");
            if(lease?.PressureCancellation.IsCancellationRequested==true)throw new WorkerResourceLimitException("内存保护已暂停后台媒体任务。");
            throw new TimeoutException("媒体工作进程任务超时。");
        }
        catch{if(!ownsGate){await gate.WaitAsync().ConfigureAwait(false);ownsGate=true;}await Stop().ConfigureAwait(false);throw;}
        finally
        {
            if(diskGuard is not null)await diskGuard.DisposeAsync().ConfigureAwait(false);
            deadline?.Dispose();lease?.Dispose();if(ownsGate)gate.Release();requestGate.Release();
        }
    }
    private async Task Start(CancellationToken cancellation,int threads,long memoryLimit)
    {
        await Stop().ConfigureAwait(false);
        instance=Guid.NewGuid().ToString("N");string name="FolderLens-"+instance,nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await Task.Run(()=>{LastTemporaryCleanup=WorkerTemporaryFiles.Recover(tempRoot);temporarySession=WorkerTemporaryFiles.Create(tempRoot,instance);taskDirectory=temporarySession.Path;},cancellation).ConfigureAwait(false);
        currentPath=null;currentInput=null;
        pipe=new(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,64*1024,64*1024);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable)!};
        foreach(string argument in new[]{"serve",name,taskDirectory!,nonce,instance})start.ArgumentList.Add(argument);
        start.Environment["VIPS_CONCURRENCY"]=threads.ToString(System.Globalization.CultureInfo.InvariantCulture);start.Environment["OMP_NUM_THREADS"]=start.Environment["VIPS_CONCURRENCY"];start.Environment["MAGICK_THREAD_LIMIT"]=start.Environment["VIPS_CONCURRENCY"];
        start.Environment["TEMP"]=taskDirectory;start.Environment["TMP"]=taskDirectory;
        process=Process.Start(start)??throw new IOException("无法启动媒体工作进程。");job=new WorkerJob(memoryLimit);job.Assign(process);
        if(priority!=WorkerPriority.Foreground)process.PriorityClass=ProcessPriorityClass.BelowNormal;
        await Task.Run(()=>temporarySession!.Bind(process),cancellation).ConfigureAwait(false);
        using var startup=CancellationTokenSource.CreateLinkedTokenSource(cancellation);startup.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);var hello=await WorkerProtocol.Read(pipe,startup.Token).ConfigureAwait(false);
            if(hello.Type!="hello"||hello.Nonce!=nonce||hello.WorkerInstanceId!=instance)throw new InvalidDataException("预览组件身份验证失败。");
            if(hello.BuildId!=WorkerProtocol.BuildId)throw new InvalidDataException("预览组件与主程序版本不一致，请更新完整程序。");
        }
        catch{await Stop().ConfigureAwait(false);throw;}
    }
    private async Task Stop()
    {
        pipe?.Dispose();pipe=null;job?.Dispose();job=null;
        if(process is not null)
        {
            if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync().ConfigureAwait(false);}process.Dispose();process=null;
        }
        var retired=temporarySession;temporarySession=null;
        if(retired is not null)LastTemporaryCleanup=await Task.Run(()=>WorkerTemporaryFiles.ReleaseExited(retired)).ConfigureAwait(false);
        producedAssets.Clear();requestedOutputs.Clear();currentPath=null;currentInput=null;currentInputFile=null;taskDirectory=null;
    }
    public ValueTask DisposeAsync()
    {lock(disposalSync)return new(disposalTask??=DisposeCore());}
    private async Task DisposeCore()
    {
        if(Interlocked.Exchange(ref disposing,1)!=0)return;
        resources.MemoryPressure-=OnMemoryPressure;lifetime.Cancel();
        await requestGate.WaitAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try{try{await idleReclaimTask.ConfigureAwait(false);}finally{await Stop().ConfigureAwait(false);}}
        finally{gate.Release();requestGate.Release();}
    }
}
