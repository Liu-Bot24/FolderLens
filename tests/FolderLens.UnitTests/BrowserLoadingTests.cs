using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class BrowserLoadingTests
{
    [Fact]
    public async Task SubsequentScanBatchPublishesWithoutManualRefreshButViewerKeepsItsSequence()
    {
        var refresh=new ScanPreviewRefresh();
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-browser-loading",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(100);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET entry_state='missing' WHERE logical_bytes>25600";return cmd.ExecuteNonQuery();});
        Assert.True(refresh.TryBegin(25,false,false,false,TimeSpan.Zero));
        var first=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        Assert.Equal(25,first.Count);
        refresh.Complete(TimeSpan.FromSeconds(1));
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET entry_state='present'";return cmd.ExecuteNonQuery();});
        Assert.False(refresh.TryBegin(100,true,false,false,TimeSpan.FromSeconds(2)));
        Assert.True(refresh.TryBegin(100,true,false,false,TimeSpan.FromSeconds(3)));
        var next=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,2);
        Assert.Equal(100,next.Count);
        Assert.Equal(25,(await catalog.ReadPage(first.Id,0)).Count);
        Assert.False(refresh.TryBegin(150,true,false,true,TimeSpan.FromSeconds(60)));
        Assert.False(refresh.TryBegin(150,true,true,false,TimeSpan.FromSeconds(60)));
        refresh.Reset();Assert.True(refresh.TryBegin(1,false,false,false,TimeSpan.Zero));
    }

    [Fact]
    public async Task EvictedPageCanBeRequestedAgainAndDemandDoesNotReadOtherPages()
    {
        var reads=new List<int>();
        using var cache=new DemandPageCache<int>((page,_)=>{reads.Add(page);return Task.FromResult<IReadOnlyList<int>>([page]);},capacity:2);
        Assert.Empty(reads);
        Assert.Equal(0,Assert.Single(await cache.Read(0,CancellationToken.None)));
        await cache.Read(1,CancellationToken.None);await cache.Read(2,CancellationToken.None);
        Assert.Equal(0,Assert.Single(await cache.Read(0,CancellationToken.None)));
        Assert.Equal(new[]{0,1,2,0},reads);
    }

    [Fact]
    public async Task RecyclingOneConsumerDoesNotCancelAnotherVisibleConsumerOfSamePage()
    {
        var finish=new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads=0;
        using var cache=new DemandPageCache<int>((_,token)=>{Interlocked.Increment(ref reads);return finish.Task.WaitAsync(token);});
        using var recycled=new CancellationTokenSource();
        var leaving=cache.Read(0,recycled.Token);var visible=cache.Read(0,CancellationToken.None);
        recycled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>leaving);
        finish.SetResult([42]);Assert.Equal(42,Assert.Single(await visible));Assert.Equal(1,reads);
    }

    [Fact]
    public async Task OffscreenQueuedPageDoesNotBlockNewVisiblePageAndCanBeRevisited()
    {
        var finish=new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads=new List<int>();
        using var cache=new DemandPageCache<int>((page,token)=>{lock(reads)reads.Add(page);return page==0?finish.Task.WaitAsync(token):Task.FromResult<IReadOnlyList<int>>([page]);},concurrency:1);
        var first=cache.Read(0,CancellationToken.None);
        using var recycled=new CancellationTokenSource();
        var offscreen=cache.Read(1,recycled.Token);var visible=cache.Read(2,CancellationToken.None);
        recycled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>offscreen);
        finish.SetResult([0]);await first;Assert.Equal(2,Assert.Single(await visible.WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.DoesNotContain(1,reads);
        Assert.Equal(1,Assert.Single(await cache.Read(1,CancellationToken.None)));
    }
}
