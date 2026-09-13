using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScanWorkerMessage(string Type,string Instance,string Nonce,string RequestId="",string? Directory=null,
    bool AllowCloud=false,ScanDirectoryPacket? Packet=null,int Version=1,string Build="folderlens-scan-0.1.0-v4",string? Document=null,string? RelativeUrl=null,string? ResolvedImage=null,string? ResourceError=null);

public static class ScanWorkerProtocol
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=16,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static async Task Write(Stream stream,ScanWorkerMessage message,CancellationToken cancellation)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(message,Json);
        if(bytes.Length>1024*1024)throw new InvalidDataException("Scan control frame exceeds its budget.");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,cancellation).ConfigureAwait(false);await stream.WriteAsync(bytes,cancellation).ConfigureAwait(false);await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
    public static async Task<ScanWorkerMessage> Read(Stream stream,CancellationToken cancellation)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,cancellation).ConfigureAwait(false);
        int length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length is <2 or >1024*1024)throw new InvalidDataException("Invalid scan frame length.");
        byte[] bytes=new byte[length];await stream.ReadExactlyAsync(bytes,cancellation).ConfigureAwait(false);
        var message=JsonSerializer.Deserialize<ScanWorkerMessage>(bytes,Json)??throw new InvalidDataException("Empty scan message.");
        if(message.Version!=1 || message.Build!="folderlens-scan-0.1.0-v4" || message.Instance.Length!=32 || message.Nonce.Length!=64 || message.Type is not ("hello" or "directory" or "stat" or "packet" or "resolveImage" or "imagePath"))throw new InvalidDataException("Scan protocol mismatch.");
        return message;
    }
    public static async Task Serve(string pipeName,string instance,string nonce,CancellationToken cancellation=default)
    {
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000,cancellation).ConfigureAwait(false);
        await Write(pipe,new("hello",instance,nonce),cancellation).ConfigureAwait(false);
        while(true)
        {
            ScanWorkerMessage message;
            try{message=await Read(pipe,cancellation).ConfigureAwait(false);}catch(EndOfStreamException){return;}
            if(message.Type is not ("directory" or "stat" or "resolveImage") || message.Instance!=instance || message.Nonce!=nonce || message.RequestId.Length!=32 || message.Directory is null)throw new InvalidDataException("Invalid scan request.");
            if(message.Type=="resolveImage")
            {
                if(message.Document is null||message.RelativeUrl is null)throw new InvalidDataException("Invalid Markdown resource request.");
                string? resolved=null,error=null;
                try{resolved=LocalResourceRules.ResolveImage(message.Directory,message.Document,message.RelativeUrl);}
                catch(UnauthorizedAccessException){error="denied";}
                catch(NotSupportedException){error="unsupported";}
                catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception or ArgumentException){error="unavailable";}
                await Write(pipe,new("imagePath",instance,nonce,message.RequestId,ResolvedImage:resolved,ResourceError:error),cancellation).ConfigureAwait(false);continue;
            }
            if(message.Type=="stat")
            {await Write(pipe,new("packet",instance,nonce,message.RequestId,Packet:ScanPathProbe.Read(message.Directory,message.AllowCloud)),cancellation).ConfigureAwait(false);continue;}
            ScanDirectoryPacket? failure=null;
            try
            {
                foreach(var packet in ScanDirectoryReader.Read(message.Directory,message.AllowCloud))
                    await Write(pipe,new("packet",instance,nonce,message.RequestId,Packet:packet),cancellation).ConfigureAwait(false);
            }
            catch(UnauthorizedAccessException){failure=new("inaccessible",[],"AccessDenied");}
            catch(IOException){failure=new("offline",[],"EnumerationIoFailure");}
            catch(ArgumentException){failure=new("failed",[],"InvalidPath");}
            if(failure is not null)await Write(pipe,new("packet",instance,nonce,message.RequestId,Packet:failure),cancellation).ConfigureAwait(false);
        }
    }
}

/// <summary>One worker per scan, reused across directories. Cancellation kills the owning Job and awaits process exit.</summary>
public sealed class ScanWorkerUnavailableException(string message,Exception? inner=null):IOException(message,inner);

public sealed class ScanWorkerClient(string executable,TimeSpan? operationTimeout=null) : IAsyncDisposable
{
    public static string? FindExecutable(string? baseDirectory=null)
    {
        string directory=baseDirectory??AppContext.BaseDirectory;
        return new[]{Path.Combine(directory,"scan-worker","FolderLens.Scan.Worker.exe"),Path.Combine(directory,"FolderLens.Scan.Worker","FolderLens.Scan.Worker.exe"),Path.Combine(directory,"FolderLens.Scan.Worker.exe")}.FirstOrDefault(HasRuntimeFiles);
    }
    private static bool HasRuntimeFiles(string path)=>File.Exists(path)&&new[]{".dll",".deps.json",".runtimeconfig.json"}.All(extension=>File.Exists(Path.ChangeExtension(path,null)+extension));
    private Process? process;private NamedPipeServerStream? pipe;private WorkerJob? job;
    private readonly string instance=Guid.NewGuid().ToString("N"),nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private int busy;
    public int? ProcessId=>process is {HasExited:false}?process.Id:null;
    public async IAsyncEnumerable<ScanDirectoryPacket> Read(string directory,bool allowCloud,[EnumeratorCancellation]CancellationToken cancellation)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("A scan worker has one active directory request.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(operationTimeout??TimeSpan.FromSeconds(20));
        bool terminal=false;
        try
        {
            if(process is null)await Start(timeout.Token,cancellation).ConfigureAwait(false);
            string request=Guid.NewGuid().ToString("N");
            try{await ScanWorkerProtocol.Write(pipe!,new("directory",instance,nonce,request,PathRules.ValidateSource(directory),allowCloud),timeout.Token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("扫描目录请求超时。");}
            while(true)
            {
                ScanWorkerMessage response;
                try{response=await ScanWorkerProtocol.Read(pipe!,timeout.Token).ConfigureAwait(false);}
                catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("扫描目录的 I/O 超时。");}
                if(response.Type!="packet" || response.Instance!=instance || response.Nonce!=nonce || response.RequestId!=request || response.Packet is null || response.Packet.Entries.Length>128)throw new InvalidDataException("Mismatched scan response.");
                // DB/probe work in the consumer is not blocked enumeration I/O.
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                var packet=response.Packet;
                foreach(var entry in packet.Entries)
                    if(string.IsNullOrEmpty(entry.Name) || entry.Name is "." or ".." || entry.Name.IndexOfAny(['\\','/',':','\0'])>=0 || entry.Bytes<0)throw new InvalidDataException("Invalid scan entry.");
                terminal=packet.State is "completed" or "excluded" or "inaccessible" or "offline" or "failed";
                yield return packet;
                if(terminal)break;
                if(packet.State is not ("started" or "batch"))throw new InvalidDataException("Invalid scan state.");
                timeout.CancelAfter(operationTimeout??TimeSpan.FromSeconds(20));
            }
        }
        finally
        {
            // If the consumer stops enumeration before its terminal packet, unread frames cannot contaminate the next directory.
            if(!terminal || timeout.IsCancellationRequested || cancellation.IsCancellationRequested)await Stop().ConfigureAwait(false);
            Interlocked.Exchange(ref busy,0);
        }
    }
    public async Task<ScanDirectoryPacket> Probe(string path,CancellationToken cancellation,bool allowCloud=false)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("A scan worker has one active request.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(operationTimeout??TimeSpan.FromSeconds(20));
        bool completed=false;
        try
        {
            if(process is null)await Start(timeout.Token,cancellation).ConfigureAwait(false);
            string request=Guid.NewGuid().ToString("N");await ScanWorkerProtocol.Write(pipe!,new("stat",instance,nonce,request,PathRules.ValidateSource(path),allowCloud),timeout.Token).ConfigureAwait(false);
            var reply=await ScanWorkerProtocol.Read(pipe!,timeout.Token).ConfigureAwait(false);
            if(reply.Type!="packet"||reply.Instance!=instance||reply.Nonce!=nonce||reply.RequestId!=request||reply.Packet is null||reply.Packet.Entries.Length!=0||reply.Packet.State is not ("present" or "missing" or "offline" or "inaccessible" or "excluded"))throw new InvalidDataException("Invalid stat response.");
            completed=true;return reply.Packet;
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("路径身份核验超时。");}
        finally{if(!completed)await Stop().ConfigureAwait(false);Interlocked.Exchange(ref busy,0);}
    }
    public async Task<string> ResolveImage(string root,string document,string relativeUrl,CancellationToken cancellation)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("A scan worker has one active request.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(operationTimeout??TimeSpan.FromSeconds(20));
        bool completed=false;
        try
        {
            if(process is null)await Start(timeout.Token,cancellation).ConfigureAwait(false);
            string request=Guid.NewGuid().ToString("N");
            await ScanWorkerProtocol.Write(pipe!,new("resolveImage",instance,nonce,request,PathRules.ValidateSource(root),Document:PathRules.ValidateSource(document),RelativeUrl:relativeUrl),timeout.Token).ConfigureAwait(false);
            var reply=await ScanWorkerProtocol.Read(pipe!,timeout.Token).ConfigureAwait(false);
            if(reply.Type!="imagePath"||reply.Instance!=instance||reply.Nonce!=nonce||reply.RequestId!=request||((reply.ResolvedImage is null)==(reply.ResourceError is null))||reply.ResourceError is not (null or "denied" or "unsupported" or "unavailable"))throw new InvalidDataException("Invalid Markdown resource response.");
            completed=true;
            return reply.ResourceError switch
            {
                "denied"=>throw new UnauthorizedAccessException("Markdown 图片不在允许范围内。"),
                "unsupported"=>throw new NotSupportedException("该格式不用于 Markdown 内嵌预览。"),
                "unavailable"=>throw new IOException("Markdown 图片暂不可用。"),
                _=>PathRules.ValidateSource(reply.ResolvedImage!)
            };
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("Markdown 图片路径核验超时。");}
        finally{if(!completed)await Stop().ConfigureAwait(false);Interlocked.Exchange(ref busy,0);}
    }
    private async Task Start(CancellationToken cancellation,CancellationToken callerCancellation)
    {
        if(!HasRuntimeFiles(executable))throw new ScanWorkerUnavailableException("目录扫描组件不完整，请使用完整的程序目录重新启动。");
        string pipeName="FolderLens-scan-"+instance;
        pipe=new(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable)!};
        foreach(string arg in new[]{"serve",pipeName,instance,nonce})info.ArgumentList.Add(arg);
        try
        {
            process=Process.Start(info)??throw new IOException("无法启动目录扫描进程。");job=new WorkerJob(256L*1024*1024);job.Assign(process);
            var connected=pipe.WaitForConnectionAsync(cancellation);
            var exited=process.WaitForExitAsync(cancellation);
            if(await Task.WhenAny(connected,exited).ConfigureAwait(false)==exited)
            {await exited.ConfigureAwait(false);throw new ScanWorkerUnavailableException($"目录扫描组件在连接前退出（代码 {process.ExitCode}）。");}
            await connected.ConfigureAwait(false);
            var hello=await ScanWorkerProtocol.Read(pipe,cancellation).ConfigureAwait(false);
            if(hello.Type!="hello"||hello.Instance!=instance||hello.Nonce!=nonce)throw new InvalidDataException("Invalid scan handshake.");
        }
        catch(OperationCanceledException error)
        {
            await Stop().ConfigureAwait(false);
            callerCancellation.ThrowIfCancellationRequested();
            throw new ScanWorkerUnavailableException("目录扫描组件启动或握手超时，请检查程序组件后重试。",error);
        }
        catch(ScanWorkerUnavailableException){await Stop().ConfigureAwait(false);throw;}
        catch(Exception error){await Stop().ConfigureAwait(false);throw new ScanWorkerUnavailableException("目录扫描组件启动或连接失败。",error);}
    }
    private async Task Stop()
    {
        pipe?.Dispose();pipe=null;job?.Dispose();job=null;
        if(process is not null)
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            if(!await Task.Run(()=>process.WaitForExit(5000)).ConfigureAwait(false))throw new TimeoutException("目录扫描进程未能在终止后退出。");
            process.Dispose();process=null;
        }
    }
    public async ValueTask DisposeAsync()=>await Stop().ConfigureAwait(false);
}
