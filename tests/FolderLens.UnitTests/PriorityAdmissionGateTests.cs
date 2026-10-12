using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PriorityAdmissionGateTests
{
    [Fact] public async Task NewViewportWinsAfterPreviousVisibleRequestLeavesViewport()
    {
        var gate=new PriorityAdmissionGate(1);await gate.WaitAsync();
        bool oldVisible=true,newVisible=false;
        var old=gate.WaitAsync(()=>oldVisible,default);var next=gate.WaitAsync(()=>newVisible,default);
        oldVisible=false;newVisible=true;gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(old.IsCompleted);
        gate.Release();await old.WaitAsync(TimeSpan.FromSeconds(2));gate.Release();Assert.Equal(1,gate.CurrentCount);
    }
    [Fact] public async Task ScrollPromotesQueuedVisibleWorkBeforeBufferedWork()
    {
        var gate=new PriorityAdmissionGate(1);await gate.WaitAsync(()=>false,default);
        bool scrolled=false;var buffered=gate.WaitAsync(()=>false,default);var visible=gate.WaitAsync(()=>scrolled,default);
        scrolled=true;gate.Release();await visible.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(buffered.IsCompleted);
        gate.Release();await buffered.WaitAsync(TimeSpan.FromSeconds(2));gate.Release();
    }
    [Fact] public async Task CancellationDoesNotLeakCapacityOrBlockFollowingWork()
    {
        var gate=new PriorityAdmissionGate(1);await gate.WaitAsync(()=>false,default);
        using var stop=new CancellationTokenSource();var cancelled=gate.WaitAsync(()=>true,stop.Token);stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
        var next=gate.WaitAsync(()=>false,default);gate.Release();await next.WaitAsync(TimeSpan.FromSeconds(2));gate.Release();
        await gate.WaitAsync(()=>true,default);gate.Release();Assert.Throws<SemaphoreFullException>(gate.Release);
    }
}
