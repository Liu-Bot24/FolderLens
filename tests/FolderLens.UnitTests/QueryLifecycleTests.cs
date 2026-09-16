using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class QueryLifecycleTests
{
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-query-tests",Guid.NewGuid().ToString("N"));
    private static async Task<T> Sessions<T>(string path,Func<SqliteConnection,T> action)
    {
        await using var db=new DatabaseExecutor(Path.Combine(path,"sessions.sqlite"));return await db.Execute(action);
    }
    private static long Scalar(SqliteConnection c,string text){using var cmd=c.CreateCommand();cmd.CommandText=text;return Convert.ToInt64(cmd.ExecuteScalar());}

    [Fact]
    public async Task PendingViewHasSeparateConfirmedCountAndLeasedHistorySurvivesCleanup()
    {
        string directory=Temp();await using var catalog=new CatalogStore(directory);await catalog.Initialize();await catalog.SeedBenchmark(40);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET is_animated=0 WHERE entry_id IN (SELECT entry_id FROM Files ORDER BY entry_id LIMIT 10)";return cmd.ExecuteNonQuery();});
        var f=new FilterSpec{RootId="benchmark",Animation="static",IncludePending=true};
        Assert.Equal(30,(await catalog.ReadFirstPage(f)).Items.Count);
        Assert.Equal(10,(await catalog.ReadFirstPage(f with{IncludePending=false})).Items.Count);
        var handle=await catalog.CreateSnapshot(f,1,1);Assert.Equal(30,handle.Count);Assert.Equal(10,handle.ConfirmedMatchCount);Assert.True(handle.IsPendingView);
        Assert.True(await catalog.RetainSnapshot(handle.Id));
        for(int i=2;i<=5;i++)await catalog.CreateSnapshot(f with{IncludePending=false},1,i);
        Assert.Equal(30,(await catalog.ReadPage(handle.Id,0)).Count);
        // Two histories plus active; an explicit lease cannot be silently evicted.
        Assert.Equal(3,await Sessions(directory,c=>Scalar(c,"SELECT count(*) FROM ResultSessions")));
        Assert.True(await catalog.ReleaseSnapshot(handle.Id));
        await catalog.CreateSnapshot(f,1,6);Assert.Empty(await catalog.ReadPage(handle.Id,0));
        Assert.True(await Sessions(directory,c=>Scalar(c,"SELECT count(*) FROM ResultDirectories"))>0);
    }

    [Fact]
    public async Task TimeoutAndDiskFullNeverPublishPartialReady()
    {
        string timeout=Temp();await using(var catalog=new CatalogStore(timeout,new SnapshotLimits{SoftDeadline=TimeSpan.Zero,HardDeadline=TimeSpan.FromTicks(1)}))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(4096);
            var timedOut=await Assert.ThrowsAsync<TimeoutException>(()=>catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Sort=new("allocatedBytes")},1,1));
            Assert.Equal("Timeout",timedOut.Data["FolderLens.SnapshotFailure"]);
            Assert.True(Guid.TryParse(timedOut.Data["FolderLens.CandidateId"] as string,out _));
            Assert.Equal(0,await Sessions(timeout,c=>Scalar(c,"SELECT count(*) FROM ResultSessions WHERE state='ready'")));
        }
        string small=Temp();await using(var catalog=new CatalogStore(small,new SnapshotLimits{SessionDiskBytes=262144}))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(4096);
            var error=await Record.ExceptionAsync(()=>catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1));
            Assert.True(error is IOException or SqliteException { SqliteErrorCode:13 },$"Expected session disk budget failure, got {error}");
            Assert.Contains(error.Data["FolderLens.SnapshotFailure"] as string,new[]{"SessionDiskLimit","DiskFull"});
            Assert.Equal(0,await Sessions(small,c=>Scalar(c,"SELECT count(*) FROM ResultSessions WHERE state='ready'")));
            Assert.Equal(0,await Sessions(small,c=>Scalar(c,"SELECT count(*) FROM ResultSessions WHERE state='building'")));
        }
    }

    [Fact]
    public async Task CancellationInterruptsRunningSqlAndExecutorRemainsUsable()
    {
        await using var catalog=new CatalogStore(Temp());await catalog.Initialize();
        using var stop=new CancellationTokenSource();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query=catalog.Read(c=>
        {
            entered.SetResult();return Scalar(c,"WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<1000000000) SELECT sum(v) FROM n");
        },stop.Token);
        await entered.Task;stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await query);
        Assert.Equal(42,await catalog.Read(c=>Scalar(c,"SELECT 42")));
        await catalog.SeedBenchmark(40);
        using var alreadyCancelled=new CancellationTokenSource();alreadyCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1,alreadyCancelled.Token));
        Assert.Equal(40,(await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,2)).Count);
    }
}
