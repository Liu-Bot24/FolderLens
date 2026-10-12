using System.Reflection;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ForegroundDatabaseTests
{
    private static string Scratch()=>Path.Combine(Path.GetTempPath(),"FolderLens-foreground",Guid.NewGuid().ToString("N"));

    [Fact] public async Task SelectionReadOvertakesQueuedOrdinaryReadAndShutdownDrainsBoth()
    {
        await using var database=new DatabaseExecutor(Path.Combine(Scratch(),"queue.sqlite"));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();var order=new List<string>();
        var running=database.Execute(_=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();return 0;});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ordinary=database.Execute(_=>{order.Add("ordinary");return 1;});
            var foreground=database.Execute(_=>{order.Add("selection");return 2;},foreground:true);
            var shutdown=database.DisposeAsync().AsTask();release.Set();
            await Task.WhenAll(running,ordinary,foreground,shutdown).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[]{"selection","ordinary"},order);
        }
        finally{release.Set();await running;}
    }

    [Fact] public async Task CancelledSelectionDoesNotExecuteOrBlockOrdinaryWork()
    {
        await using var database=new DatabaseExecutor(Path.Combine(Scratch(),"queue.sqlite"));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();using var cancellation=new CancellationTokenSource();bool executed=false;
        var running=database.Execute(_=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();return 0;});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelled=database.Execute(_=>{executed=true;return 1;},cancellation.Token,foreground:true);
            var ordinary=database.Execute(_=>7);cancellation.Cancel();release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
            Assert.Equal(7,await ordinary.WaitAsync(TimeSpan.FromSeconds(5)));Assert.False(executed);
        }
        finally{release.Set();await running;}
    }

    [Fact] public async Task CurrentPageDoesNotWaitForSnapshotComparisonLane()
    {
        await using var catalog=new CatalogStore(Scratch());await catalog.Initialize();await catalog.SeedBenchmark(10);
        var snapshot=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        // Hold the actual lane used by CompareSnapshots, without timing a large
        // SQL query or relying on machine speed to create an overlap.
        var lane=(DatabaseExecutor)typeof(CatalogStore).GetField("sessionComparisonReader",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(catalog)!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();
        var blocked=lane.Execute(_=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();return 0;});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var page=await catalog.ReadPage(snapshot.Id,0,1,foreground:true).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("000000000001",Assert.Single(page).EntryId);Assert.False(blocked.IsCompleted);
        }
        finally{release.Set();await blocked;}
    }

    [Fact] public async Task GroupedInitialPageRetainsFiltersWithoutRequiringGroupAggregation()
    {
        await using var catalog=new CatalogStore(Scratch());await catalog.Initialize();await catalog.SeedBenchmark(300);
        var filter=new FilterSpec{RootId="benchmark",NamePathQuery="file2",Grouping=new(true,"first","logicalBytes","desc","all")};
        var groupedPreview=await catalog.ReadFirstPage(filter,deferGrouping:true);
        var ungrouped=await catalog.ReadFirstPage(filter with{Grouping=new()});
        Assert.NotEmpty(groupedPreview.Items);Assert.True(groupedPreview.Items.Count<=256);
        Assert.Equal(ungrouped.Items.Select(item=>item.EntryId),groupedPreview.Items.Select(item=>item.EntryId));
        Assert.DoesNotContain(groupedPreview.Items,item=>item.RelativePath=="file1.jpg");
    }
}
