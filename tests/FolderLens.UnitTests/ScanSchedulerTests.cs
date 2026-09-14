using FolderLens.Infrastructure;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class ScanSchedulerTests
{
    [Fact]
    public async Task ThirdDisplayedRootRunsBeforeOlderRootsFinishAndTheyCanResume()
    {
        var scheduler=new ScanScheduler();using var a=await scheduler.Enter("a",default);var b=await scheduler.Enter("b",default);
        var oldNext=scheduler.Enter("a",default);scheduler.Prefer("c");var current=scheduler.Enter("c",default);
        Assert.False(current.IsCompleted);b.Dispose();using var c=await current.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(oldNext.IsCompleted);Assert.Equal(2,scheduler.Counts.Active);
        a.Dispose();using var resumed=await oldNext.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(2,scheduler.Counts.Active);
    }
    [Fact]
    public async Task CancellingQueuedRootDoesNotLeakTurnOrCancelOtherRoots()
    {
        var scheduler=new ScanScheduler();using var a=await scheduler.Enter("a",default);using var b=await scheduler.Enter("b",default);
        using var stop=new CancellationTokenSource();var cancelled=scheduler.Enter("c",stop.Token);stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);Assert.Equal(0,scheduler.Counts.Waiting);
        b.Dispose();using var next=await scheduler.Enter("d",default).WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(2,scheduler.Counts.Active);
    }
}
