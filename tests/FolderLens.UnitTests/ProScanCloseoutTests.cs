using System.Diagnostics;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ProScanCloseoutTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(31)]
    public async Task ExplicitStopPublishesDeferredCommittedFilesAfterRetirement(int count)
    {
        string directory=Fixture();await using var catalog=new CatalogStore(directory);await catalog.Initialize();await catalog.SeedBenchmark(count);
        var filter=new FilterSpec{RootId="benchmark",Kinds=[]};
        Assert.True(ScanPreviewRefresh.DeferFirstBatch((await catalog.ReadFirstPage(filter)).Items.Count,true));
        var retirement=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopped=new CancellationTokenSource();stopped.Cancel();
        long? displayed=null;
        var publish=StoppedScanPublication.Run(retirement.Task,CancellationToken.None,()=>true,async()=>displayed=(await catalog.CreateSnapshot(filter,1,1)).Count);
        Assert.Null(displayed);retirement.SetCanceled(stopped.Token);await publish;
        Assert.Equal(count,displayed);Assert.True(stopped.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedScanNeverPublishesIntoAnotherRootOrClosingApplication(bool closing)
    {
        var retirement=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime=new CancellationTokenSource();bool current=true,published=false;
        var publish=StoppedScanPublication.Run(retirement.Task,lifetime.Token,()=>current,()=>{published=true;return Task.CompletedTask;});
        if(closing)lifetime.Cancel();else current=false;
        retirement.SetResult();
        if(closing)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>publish);else await publish;
        Assert.False(published);
    }

    [Fact]
    public async Task SnapshotRecoversOversizedCheckpointedWalWithoutAnotherBusinessWrite()
    {
        string directory=Fixture();
        await using var catalog=new CatalogStore(directory,new SnapshotLimits{CatalogWalBytes=4L<<20});
        await catalog.Initialize();await catalog.SeedBenchmark(20);await catalog.CheckpointCatalog(true);
        using var capacity=DatabaseExecutor.Open(Path.Combine(directory,"catalog.sqlite"));
        using var held=capacity.BeginTransaction(deferred:true);
        using(var q=capacity.CreateCommand()){q.Transaction=held;q.CommandText="SELECT count(*) FROM Files";Assert.Equal(20L,q.ExecuteScalar());}
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="CREATE TABLE WalFixture(payload BLOB); INSERT INTO WalFixture VALUES(zeroblob(8388608));";return q.ExecuteNonQuery();});
        var pinned=await catalog.CheckpointCatalog();Assert.True(pinned.Bytes>4L<<20);
        var watch=Stopwatch.StartNew();var busy=await catalog.PrepareSnapshotWal();
        Assert.NotEqual(0,busy.Busy);
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE Files SET logical_bytes=logical_bytes+1";return q.ExecuteNonQuery();});
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(1),$"High-water maintenance blocked the writer for {watch.Elapsed.TotalMilliseconds:N0} ms.");
        Assert.Equal(5000L,await catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="PRAGMA busy_timeout";return (long)q.ExecuteScalar()!;}));
        held.Rollback();
        var checkpoint=await catalog.CheckpointCatalog();
        Assert.Equal(checkpoint.LogFrames,checkpoint.CheckpointedFrames);Assert.True(checkpoint.Bytes>4L<<20);
        var snapshot=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1);
        Assert.Equal(20,snapshot.Count);
        Assert.True(new FileInfo(Path.Combine(directory,"catalog.sqlite-wal")).Length<4L<<20);
    }

    [Fact]
    public async Task RefusedRefreshDoesNotAdvanceObservedEpochOrLoseFiles()
    {
        string directory=Fixture(),source=Path.Combine(directory,"source");Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source,"visible.jpg"),"fixture");
        await using var catalog=new CatalogStore(Path.Combine(directory,"db"));await catalog.Initialize();
        var resolver=new RootIdentityResolver(catalog,Worker());var opened=await resolver.Open(source);
        await new DirectoryIndexer(catalog,Worker()).Scan(opened.RootId,source,opened.Epoch,true,[],null,CancellationToken.None);
        var filter=new FilterSpec{RootId=opened.RootId,ObservedRootEpoch=opened.Epoch,Kinds=[]};
        Assert.Single((await catalog.ReadFirstPage(filter)).Items);
        catalog.MarkBrowsingBudgetReached();
        await Assert.ThrowsAsync<BrowsingBudgetException>(()=>resolver.Open(source));
        long epoch=await catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="SELECT root_epoch FROM Roots WHERE root_id=$r";q.Parameters.AddWithValue("$r",opened.RootId);return (long)q.ExecuteScalar()!;});
        Assert.Equal(opened.Epoch,epoch);
        Assert.Single((await catalog.ReadFirstPage(filter)).Items);
    }

    [Fact]
    public async Task NavigationResumesStoppedScopeAfterRetiringItsWork()
    {
        string directory=Fixture(),source=Path.Combine(directory,"source"),child=Path.Combine(source,"child");Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child,"old.jpg"),"fixture");
        await using var catalog=new CatalogStore(Path.Combine(directory,"db"));await catalog.Initialize();long epoch=await catalog.OpenRoot("r",source);
        var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("r",source,epoch,true,[],null,CancellationToken.None);
        using var stopped=new CancellationTokenSource();stopped.Cancel();
        var retiring=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=RootTaskRetirement.ResumeForNavigation(stopped,retiring.Task,null,CancellationToken.None);
        Assert.False(resume.IsCompleted);retiring.SetCanceled(stopped.Token);
        using var current=await resume;Assert.False(current.IsCancellationRequested);Assert.True(stopped.IsCancellationRequested);
        File.WriteAllText(Path.Combine(child,"new.jpg"),"fixture");
        await new ScanDirtyDirectories(catalog).Mark("r",epoch,[new("child","BrowseNavigation",true)],current.Token);
        var result=await scanner.ReconcileDirty("r",source,epoch,true,[],null,current.Token);
        Assert.Equal("ready",result.State);Assert.Equal(1,result.Directories);
        Assert.Equal(2,(await catalog.ReadFirstPage(new FilterSpec{RootId="r",ObservedRootEpoch=epoch,DirectoryScope="child",Kinds=[]})).Items.Count);
    }

    [Fact]
    public async Task NavigationDoesNotReplaceOrWaitForAnUncancelledScan()
    {
        using var current=new CancellationTokenSource();var running=new TaskCompletionSource();
        Assert.Same(current,await RootTaskRetirement.ResumeForNavigation(current,running.Task,null,CancellationToken.None));
        using var stopped=new CancellationTokenSource();stopped.Cancel();using var lifetime=new CancellationTokenSource();lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>RootTaskRetirement.ResumeForNavigation(stopped,null,null,lifetime.Token));
    }

    [Fact]
    public async Task PeriodicHintDuringTraversalDoesNotScheduleAnotherTreeButRealChangesSurvive()
    {
        string directory=Fixture();await using var catalog=new CatalogStore(directory);await catalog.Initialize();await catalog.SeedBenchmark(1);
        var dirty=new ScanDirtyDirectories(catalog);
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="INSERT INTO ScanRuns(scan_id,root_id,root_epoch,state,started_utc_ticks) VALUES('active','benchmark',1,'running',1)";return q.ExecuteNonQuery();});
        await dirty.Mark("benchmark",1,[new("","PeriodicReconcile",true)],onlyIfMissing:true);
        Assert.Empty(await dirty.Read("benchmark",1));
        await dirty.Mark("benchmark",1,[new("","FileChanged",true)]);
        var old=Assert.Single(await dirty.Read("benchmark",1));
        await dirty.Mark("benchmark",1,[new("","WatcherEventsLost",true)]);
        await dirty.Acknowledge("benchmark",1,old);
        Assert.Single(await dirty.Read("benchmark",1));
        await dirty.Acknowledge("benchmark",1,(await dirty.Read("benchmark",1)).Single());
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE ScanRuns SET state='completed' WHERE scan_id='active'";return q.ExecuteNonQuery();});
        await dirty.Mark("benchmark",1,[new("","PeriodicReconcile",true)],onlyIfMissing:true);
        Assert.Single(await dirty.Read("benchmark",1));
    }

    [Fact]
    public async Task SnapshotDoesNotWaitForOldCapacityReaderToReleaseWal()
    {
        string directory=Fixture();await using var catalog=new CatalogStore(directory);await catalog.Initialize();await catalog.SeedBenchmark(20);
        await catalog.CheckpointCatalog(true);
        using var capacity=DatabaseExecutor.Open(Path.Combine(directory,"catalog.sqlite"));
        using var held=capacity.BeginTransaction(deferred:true);
        using(var q=capacity.CreateCommand()){q.Transaction=held;q.CommandText="SELECT count(*) FROM Files";Assert.Equal(20L,q.ExecuteScalar());}
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="PRAGMA busy_timeout=2000; UPDATE Files SET logical_bytes=logical_bytes+1";return q.ExecuteNonQuery();});
        var watch=Stopwatch.StartNew();
        var snapshot=catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1);
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE Files SET logical_bytes=logical_bytes+1";return q.ExecuteNonQuery();});
        var result=await snapshot;
        Assert.Equal(20,result.Count);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(1),$"Snapshot blocked the writer for {watch.Elapsed.TotalMilliseconds:N0} ms while an older reader was open.");
    }

    private static string Fixture(){string path=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string Worker()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
        return Path.Combine(root!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
    }
}
