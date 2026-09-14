using FolderLens.Infrastructure;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class ScanAdmissionRegressionTests
{
    [Fact]
    public async Task NewRootProducesRealFileResultsWhileOldRootIsStillScanning()
    {
        string folder=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        foreach(string name in new[]{"a","b","c"}){string path=Path.Combine(folder,name);Directory.CreateDirectory(path);for(int i=0;i<(name=="c"?1:140);i++)File.WriteAllText(Path.Combine(path,$"{i}.jpg"),"fixture");}
        await using var catalog=new CatalogStore(Path.Combine(folder,"db"));await catalog.Initialize();
        var scheduler=new ScanScheduler();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enteredA=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var enteredB=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseA=new ManualResetEventSlim();using var releaseB=new ManualResetEventSlim();
        async Task<ScanProgress> Scan(string name,IProgress<ScanProgress>? progress)
        {string source=Path.Combine(folder,name);long epoch=await catalog.OpenRoot(name,source);return await new DirectoryIndexer(catalog){Scheduler=scheduler}.Scan(name,source,epoch,true,[],progress,stop.Token);}
        var a=Task.Run(()=>Scan("a",new InlineProgress(p=>{if(p.Files>0&&!enteredA.Task.IsCompleted){enteredA.SetResult();releaseA.Wait(stop.Token);}})));
        var b=Task.Run(()=>Scan("b",new InlineProgress(p=>{if(p.Files>0&&!enteredB.Task.IsCompleted){enteredB.SetResult();releaseB.Wait(stop.Token);}})));
        Task<ScanProgress>? current=null;
        try
        {
            await Task.WhenAll(enteredA.Task,enteredB.Task).WaitAsync(stop.Token);scheduler.Prefer("c");current=Task.Run(()=>Scan("c",null));
            while(!scheduler.IsWaiting("c")){await Task.Delay(10,stop.Token);}
            releaseA.Set();var result=await current.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ready",result.State);Assert.Equal(1,result.Files);Assert.False(b.IsCompleted);
            long files=await catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="SELECT count(*) FROM Files WHERE root_id='c'";return (long)q.ExecuteScalar()!;});Assert.Equal(1,files);
        }
        finally{releaseA.Set();releaseB.Set();await Task.WhenAll(a,b);if(current is not null)await current;}
        Assert.Equal(140,(await a).Files);Assert.Equal(140,(await b).Files);
    }
    private sealed class InlineProgress(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
}
