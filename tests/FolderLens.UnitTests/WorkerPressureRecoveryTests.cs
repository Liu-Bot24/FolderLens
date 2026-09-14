using System.Diagnostics;
using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class WorkerPressureRecoveryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact] public async Task ClosingCancelsPendingAdmissionAndAllDisposersWaitForCleanup()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-pressure",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"source.txt");File.WriteAllText(input,"generated fixture");
        var resources=new WorkerResources(new(128L<<20,16L<<30,64L<<20,512L<<20,0,0,0,8,true,true));
        var worker=new WorkerClient("unused.exe",directory,WorkerPriority.Visible,resources);
        var pending=worker.Request(input,"probe",new("fixture",1,1,1,1,1),new(1,1),CancellationToken.None);
        Assert.False(pending.IsCompleted);
        var first=worker.DisposeAsync().AsTask();var second=worker.DisposeAsync().AsTask();Assert.Same(first,second);
        await first.WaitAsync(TimeSpan.FromSeconds(2));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.Equal(0,resources.Snapshot.Pending);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task IdleNativeMemoryIsReclaimedBeforeBackgroundAdmission(bool retainedAsset)
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"FolderLens.slnx")))root=root.Parent;
        string executable=Path.Combine(root!.FullName,"tests","FolderLens.ScanFixture","bin","Release","net10.0-windows10.0.26100.0","FolderLens.ScanFixture.exe");
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-pressure",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"source.txt");File.WriteAllText(input,"generated fixture");
        var resources=new WorkerResources(new(0,16L<<30,64L<<20,512L<<20,0,0,0,8,false,true));
        await using var worker=new WorkerClient(executable,Path.Combine(directory,"worker"),WorkerPriority.Visible,resources);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context=new RequestContext("fixture",1,1,1,1,1);
        var first=await worker.Request(input,retainedAsset?"fit":"probe",context,new(128,1),stop.Token);
        using var original=Process.GetProcessById(first.Message.Metadata!.Value.GetProperty("pid").GetInt32());original.Refresh();
        long retained=original.PrivateMemorySize64;Assert.True(retained>64L<<20,$"Native fixture private bytes: {retained}");
        output.WriteLine($"Retained native private bytes={retained}; soft limit={64L<<20}; retained asset={retainedAsset}");
        Assert.Equal(0,resources.Snapshot.ActiveBackground);
        void Sample()
        {
            long bytes=0;if(!original.HasExited){original.Refresh();bytes=original.PrivateMemorySize64;}
            resources.ObserveMemory(bytes,16L<<30,64L<<20,512L<<20,bytes>=64L<<20,true);
        }
        Sample();Assert.True(resources.Snapshot.UnderPressure);
        await using var sampler=new Timer(_=>Sample(),null,TimeSpan.FromMilliseconds(50),TimeSpan.FromMilliseconds(50));
        Task<ImageReply>? next=null;
        try
        {
            next=worker.Request(input,"probe",context with{SelectionGeneration=2},new(1,1),stop.Token);
            if(retainedAsset)
            {
                await Task.Delay(150,stop.Token);Assert.False(original.HasExited);Assert.True(File.Exists(first.AssetPath));Assert.False(next.IsCompleted);
                await worker.ReleaseAsset(first).WaitAsync(TimeSpan.FromSeconds(1));
            }
            var completed=await Task.WhenAny(next,Task.Delay(3000,stop.Token));
            Assert.Same(next,completed);var reply=await next;
            Assert.True(original.HasExited);Assert.NotEqual(first.Message.WorkerInstanceId,reply.Message.WorkerInstanceId);
        }
        finally{stop.Cancel();if(next is not null)try{await next;}catch(OperationCanceledException){} }
    }
}
