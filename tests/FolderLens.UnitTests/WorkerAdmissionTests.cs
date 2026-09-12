using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class WorkerAdmissionTests
{
    private static WorkerResources Coordinator(int cpu=16,bool pressure=false)=>new(new(0,16L<<30,1L<<30,8L<<30,0,0,0,cpu,pressure,true));
    [Theory][InlineData(2,1,2)][InlineData(6,6,2)][InlineData(7,6,3)][InlineData(8,1,2)][InlineData(12,2,4)][InlineData(16,6,8)][InlineData(16,1,2)]
    public async Task ThumbnailCapacityUsesCpuAndMemoryWithoutTakingForegroundBudget(int cpu,int memoryGiB,int expected)
    {
        var coordinator=new WorkerResources(new(0,16L<<30,(long)memoryGiB<<30,8L<<30,0,0,0,cpu,false,true));
        Assert.Equal(expected,coordinator.ThumbnailConcurrency);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));var leases=new List<WorkerResources.Lease>();
        try
        {
            for(int i=0;i<expected;i++)leases.Add(await coordinator.Acquire(WorkerPriority.Visible,stop.Token));
            Assert.True(leases.Sum(item=>item.CpuThreads)<=cpu);
            var pending=coordinator.Acquire(WorkerPriority.Visible,stop.Token);Assert.False(pending.IsCompleted);
            if(cpu>=6){using var foreground=await coordinator.Acquire(WorkerPriority.Foreground,stop.Token);Assert.True(leases.Sum(item=>item.CpuThreads)+foreground.CpuThreads<=cpu);}
            stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending);
        }
        finally{foreach(var lease in leases)lease.Dispose();}
    }
    [Theory][InlineData(2)][InlineData(3)][InlineData(4)]
    public async Task QueuedForegroundDrainsBackgroundBeforeLaterWork(int cpu)
    {
        var coordinator=Coordinator(cpu);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first=await coordinator.Acquire(WorkerPriority.Visible,stop.Token);using var second=await coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        var foreground=coordinator.Acquire(WorkerPriority.Foreground,stop.Token);var later=coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        try
        {
            first.Dispose();Assert.False(later.IsCompleted);
            second.Dispose();using var ready=await foreground.WaitAsync(TimeSpan.FromSeconds(1));Assert.False(later.IsCompleted);
            ready.Dispose();using var background=await later.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally{stop.Cancel();foreach(var task in new[]{foreground,later})try{(await task).Dispose();}catch(OperationCanceledException){}}
    }
    [Fact]public async Task WaitingForegroundDoesNotBlockAnAvailableVisibleSlot()
    {
        var coordinator=Coordinator();
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held=await coordinator.Acquire(WorkerPriority.Foreground,stop.Token);
        var next=coordinator.Acquire(WorkerPriority.Foreground,stop.Token);
        var visible=coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        try
        {
            using var thumbnail=await visible.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(next.IsCompleted);
            Assert.Equal(WorkerPriority.Visible,thumbnail.Priority);
            Assert.Equal(1,coordinator.Snapshot.ActiveForeground);
            Assert.Equal(1,coordinator.Snapshot.ActiveBackground);
            held.Dispose();using var foreground=await next.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(WorkerPriority.Foreground,foreground.Priority);
        }
        finally
        {
            stop.Cancel();
            foreach(var task in new[]{next,visible})try{(await task).Dispose();}catch(OperationCanceledException){}
        }
    }
    [Fact]public async Task FullBackgroundSlotsKeepPriorityAndCancelWaitersWithoutStealingLeases()
    {
        var coordinator=Coordinator();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first=await coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        using var second=await coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        var metadata=coordinator.Acquire(WorkerPriority.Metadata,stop.Token);
        var visible=coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        try
        {
            Assert.False(metadata.IsCompleted);Assert.False(visible.IsCompleted);
            using var foreground=await coordinator.Acquire(WorkerPriority.Foreground,stop.Token).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(2,coordinator.Snapshot.ActiveBackground);Assert.Equal(1,coordinator.Snapshot.ActiveForeground);
            first.Dispose();using var ready=await visible.WaitAsync(TimeSpan.FromSeconds(1));Assert.False(metadata.IsCompleted);
            ready.Dispose();using var tail=await metadata.WaitAsync(TimeSpan.FromSeconds(1));Assert.Equal(WorkerPriority.Metadata,tail.Priority);
        }
        finally{stop.Cancel();foreach(var task in new[]{metadata,visible})try{(await task).Dispose();}catch(OperationCanceledException){}}
    }
    [Theory][InlineData(2,false)][InlineData(16,true)]
    public async Task InsufficientCpuOrMemoryPressureKeepsBackgroundPending(int cpu,bool pressure)
    {
        var coordinator=Coordinator(cpu,pressure);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var held=await coordinator.Acquire(WorkerPriority.Foreground,stop.Token);
        var background=coordinator.Acquire(WorkerPriority.Visible,stop.Token);
        Assert.False(background.IsCompleted);Assert.Equal(0,coordinator.Snapshot.ActiveBackground);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await background);
        Assert.Equal(1,coordinator.Snapshot.ActiveForeground);Assert.Equal(0,coordinator.Snapshot.Pending);
    }
}
