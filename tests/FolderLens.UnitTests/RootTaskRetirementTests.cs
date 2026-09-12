using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class RootTaskRetirementTests
{
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
