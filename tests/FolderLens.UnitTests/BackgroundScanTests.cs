using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class BackgroundScanTests
{
    [Fact]
    public async Task BrowserCancellationDoesNotRetireApplicationScanAndShutdownCancelsQueuedWork()
    {
        using var application=new CancellationTokenSource();using var browser=new CancellationTokenSource();using var slots=new SemaphoreSlim(1,1);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first=new BackgroundScan("a","a",1,new FilterSpec(),new ScanPriority(),slots,application.Token,async token=>
        {entered.SetResult();await Task.Delay(Timeout.Infinite,token);return new(0,0,0,"ready");});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bool secondStarted=false;
        using var second=new BackgroundScan("b","b",1,new FilterSpec(),new ScanPriority(),slots,application.Token,token=>
        {secondStarted=true;return Task.FromResult(new ScanProgress(0,0,0,"ready"));});
        browser.Cancel();Assert.False(first.Cancelled);Assert.False(first.Completion.IsCompleted);Assert.False(secondStarted);
        application.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>second.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(secondStarted);Assert.Equal(1,slots.CurrentCount);
    }
    [Fact]
    public async Task ExplicitCancelDoesNotCancelAnotherRoot()
    {
        using var application=new CancellationTokenSource();using var slots=new SemaphoreSlim(1,1);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first=new BackgroundScan("a","a",1,new FilterSpec(),new ScanPriority(),slots,application.Token,async token=>
        {entered.SetResult();await Task.Delay(Timeout.Infinite,token);return new(0,0,0,"ready");});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var second=new BackgroundScan("b","b",1,new FilterSpec(),new ScanPriority(),slots,application.Token,token=>Task.FromResult(new ScanProgress(1,1,0,"ready")));
        first.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first.Completion);
        Assert.Equal("ready",(await second.Completion.WaitAsync(TimeSpan.FromSeconds(2))).State);Assert.False(second.Cancelled);
    }
}
