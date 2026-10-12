using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class RootTaskRetirementTests
{
    [Fact]public async Task NewNavigationObservesOldFailureAndStillWaitsForOtherProducer()
    {
        using var retired=new CancellationTokenSource();retired.Cancel();
        var error=new IOException("previous scan failed");var observed=new List<Exception>();
        var metadata=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting=RootTaskRetirement.Wait(Task.FromException(error),metadata.Task,retired.Token,CancellationToken.None,observed.Add);
        Assert.Same(error,Assert.Single(observed));Assert.False(waiting.IsCompleted);
        metadata.SetResult();await waiting;
        using var resumed=await RootTaskRetirement.ResumeForNavigation(retired,Task.FromException(error),null,CancellationToken.None,observed.Add);
        Assert.False(resumed.IsCancellationRequested);Assert.Equal(2,observed.Count);
    }
    [Fact]public async Task FailureObserverCannotHideActiveRootFailureOrShutdown()
    {
        var observed=new List<Exception>();
        await Assert.ThrowsAsync<IOException>(()=>RootTaskRetirement.Wait(Task.FromException(new IOException("active failure")),null,CancellationToken.None,CancellationToken.None,observed.Add));
        using var stopped=new CancellationTokenSource();stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>RootTaskRetirement.Wait(Task.CompletedTask,null,stopped.Token,stopped.Token,observed.Add));
        Assert.Empty(observed);
    }
    [Fact]public async Task CancelledReconciliationDoesNotPreventSuccessiveRootOpens()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-root-retirement",Guid.NewGuid().ToString("N"));
        await using var catalog=new CatalogStore(directory);await catalog.Initialize();
        await catalog.OpenRoot("previous",directory);
        using var oldStop=new CancellationTokenSource();oldStop.Cancel();
        Task previous=new DirectoryIndexer(catalog).ReconcileDirty("previous",directory,1,true,[],null,oldStop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await previous);
        foreach(string next in new[]{"second","third","fourth"})
        {
            await RootTaskRetirement.Wait(previous,Task.CompletedTask,oldStop.Token,CancellationToken.None);
            Assert.Equal(1,await catalog.OpenRoot(next,Path.Combine(directory,next)));
        }
    }
    [Fact]public async Task RealFaultAndApplicationShutdownAreNotHidden()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAsync<IOException>(()=>RootTaskRetirement.Wait(Task.FromException(new IOException("disk failure")),null,cancelled.Token,CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>RootTaskRetirement.Wait(Task.CompletedTask,null,cancelled.Token,cancelled.Token));
    }
}
