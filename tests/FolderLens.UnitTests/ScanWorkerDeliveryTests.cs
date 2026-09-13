using System.Diagnostics;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScanWorkerDeliveryTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"FolderLens-worker-delivery-"+Guid.NewGuid().ToString("N"));
    private string CreateEntry(string relative,bool complete)
    {
        string path=Path.Combine(directory,relative,"FolderLens.Scan.Worker.exe");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"");
        if(complete)foreach(string suffix in new[]{".dll",".deps.json",".runtimeconfig.json"})File.WriteAllText(Path.ChangeExtension(path,null)+suffix,"");
        return path;
    }
    [Fact] public void DedicatedCompleteWorkerWinsOverIncompleteRootEntry()
    {
        CreateEntry("",false);Assert.Null(ScanWorkerClient.FindExecutable(directory));
        string legacy=CreateEntry("FolderLens.Scan.Worker",true);Assert.Equal(legacy,ScanWorkerClient.FindExecutable(directory));
        string standard=CreateEntry("scan-worker",true);Assert.Equal(standard,ScanWorkerClient.FindExecutable(directory));
    }
    [Fact] public async Task MissingAssemblyFailsImmediatelyWithoutConnectionTimeout()
    {
        string path=CreateEntry("",false);var timer=Stopwatch.StartNew();
        await using var worker=new ScanWorkerClient(path,TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<ScanWorkerUnavailableException>(async()=>{await foreach(var _ in worker.Read(directory,false,CancellationToken.None)){};});
        Assert.True(timer.Elapsed<TimeSpan.FromSeconds(3));Assert.Null(worker.ProcessId);
    }
    [Fact] public async Task EarlyProcessExitFailsBeforeConnectionTimeout()
    {
        string path=CreateEntry("",true);File.Copy(Path.Combine(Environment.SystemDirectory,"where.exe"),path,true);
        var timer=Stopwatch.StartNew();await using var worker=new ScanWorkerClient(path,TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<ScanWorkerUnavailableException>(async()=>{await foreach(var _ in worker.Read(directory,false,CancellationToken.None)){};});
        Assert.True(timer.Elapsed<TimeSpan.FromSeconds(5));Assert.Null(worker.ProcessId);
    }
    private string CreateLiveWorker(string mode)
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"FolderLens.slnx")))root=root.Parent;
        string built=Path.Combine(root!.FullName,"tests","FolderLens.ScanFixture","bin","Release","net10.0-windows10.0.26100.0");
        Directory.CreateDirectory(directory);
        foreach(string file in Directory.GetFiles(built))File.Copy(file,Path.Combine(directory,Path.GetFileName(file)),true);
        File.WriteAllText(Path.Combine(directory,"mode.txt"),mode);
        return Path.Combine(directory,"FolderLens.ScanFixture.exe");
    }
    [Theory]
    [InlineData("no-connect")]
    [InlineData("no-hello")]
    public async Task LiveStartupTimeoutIsComponentFailureAndReapsProcess(string mode)
    {
        await using var worker=new ScanWorkerClient(CreateLiveWorker(mode),TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ScanWorkerUnavailableException>(async()=>{await foreach(var _ in worker.Read(directory,false,CancellationToken.None)){};});
        Assert.True(File.Exists(Path.Combine(directory,"started.txt")));
        Assert.Equal(mode=="no-hello",File.Exists(Path.Combine(directory,"connected.txt")));
        Assert.False(File.Exists(Path.Combine(directory,"requested.txt")));
        Assert.Null(worker.ProcessId);
        int pid=int.Parse(File.ReadAllText(Path.Combine(directory,"started.txt")));
        Assert.Throws<ArgumentException>(()=>Process.GetProcessById(pid));
    }
    [Fact] public async Task HandshakenDirectoryTimeoutRemainsIoTimeout()
    {
        await using var worker=new ScanWorkerClient(CreateLiveWorker("no-packet"),TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(async()=>{await foreach(var _ in worker.Read(directory,false,CancellationToken.None)){};});
        Assert.Equal("directory",File.ReadAllText(Path.Combine(directory,"requested.txt")));Assert.Null(worker.ProcessId);
    }
    [Fact] public async Task UserCancellationDuringStartupRemainsCancellation()
    {
        await using var worker=new ScanWorkerClient(CreateLiveWorker("no-hello"),TimeSpan.FromSeconds(10));
        using var cancel=new CancellationTokenSource();
        var request=Task.Run(async()=>{await foreach(var _ in worker.Read(directory,false,cancel.Token)){};});
        var watch=Stopwatch.StartNew();
        while(!File.Exists(Path.Combine(directory,"connected.txt"))){Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));await Task.Delay(10);}
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request);Assert.Null(worker.ProcessId);
    }
    [Theory]
    [InlineData("no-connect")]
    [InlineData("no-hello")]
    public async Task RootResolutionDoesNotPersistOfflineForStartupFailure(string mode)
    {
        string executable=CreateLiveWorker(mode);
        await using var catalog=new CatalogStore(Path.Combine(directory,"catalog"));await catalog.Initialize();
        await Assert.ThrowsAsync<ScanWorkerUnavailableException>(()=>new RootIdentityResolver(catalog,executable,TimeSpan.FromSeconds(2)).Open(directory));
        long roots=await catalog.Read(connection=>{using var query=connection.CreateCommand();query.CommandText="SELECT COUNT(*) FROM Roots";return (long)query.ExecuteScalar()!;});
        Assert.Equal(0,roots);Assert.False(File.Exists(Path.Combine(directory,"requested.txt")));
    }
    [Theory]
    [InlineData("no-connect","stat")]
    [InlineData("no-hello","stat")]
    [InlineData("no-connect","resolveImage")]
    [InlineData("no-hello","resolveImage")]
    public async Task OtherStartupClientsPreserveComponentClassification(string mode,string operation)
    {
        await using var worker=new ScanWorkerClient(CreateLiveWorker(mode),TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ScanWorkerUnavailableException>(async()=>
        {
            if(operation=="stat")await worker.Probe(directory,CancellationToken.None);
            else await worker.ResolveImage(directory,Path.Combine(directory,"page.md"),"image.png",CancellationToken.None);
        });
        Assert.Null(worker.ProcessId);Assert.False(File.Exists(Path.Combine(directory,"requested.txt")));
    }
    [Fact] public async Task PlainProcessTerminationReleaseBaseline()
    {
        string executable=CreateLiveWorker("no-connect");
        byte[] apphost=File.ReadAllBytes(executable);
        for(int attempt=0;attempt<20;attempt++)
        {
            string started=Path.Combine(directory,"started.txt");File.Delete(started);
            var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
            foreach(string arg in new[]{"serve",Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new string('0',64)})info.ArgumentList.Add(arg);
            using(var process=Process.Start(info)!)
            {
                var watch=Stopwatch.StartNew();
                while(!File.Exists(started)){Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));await Task.Delay(1);}
                process.Kill(entireProcessTree:true);await process.WaitForExitAsync();
            }
            File.Delete(executable);File.WriteAllBytes(executable,apphost);
        }
    }
    [Fact] public async Task CancelledStartupReleasesExecutableBeforeReturning()
    {
        string executable=CreateLiveWorker("no-hello");
        for(int attempt=0;attempt<20;attempt++)
        {
            string connected=Path.Combine(directory,"connected.txt");File.Delete(connected);
            await using(var worker=new ScanWorkerClient(executable,TimeSpan.FromSeconds(10)))
            {
                using var cancel=new CancellationTokenSource();
                var request=Task.Run(async()=>{await foreach(var _ in worker.Read(directory,false,cancel.Token)){};});
                var watch=Stopwatch.StartNew();
                while(!File.Exists(connected)){Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));await Task.Delay(1);}
                cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request);
            }
            // The executable must be releasable, not merely absent from the process list.
            File.Delete(executable);
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"FolderLens.slnx")))root=root.Parent;
            File.Copy(Path.Combine(root!.FullName,"tests","FolderLens.ScanFixture","bin","Release","net10.0-windows10.0.26100.0","FolderLens.ScanFixture.exe"),executable);
        }
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
