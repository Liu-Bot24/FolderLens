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
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
