using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DesktopLifecycleTests
{
    [Fact]
    public async Task SecondaryLaunchForwardsToOwnerAndOwnershipCanBeReacquired()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-instance-tests",Guid.NewGuid().ToString("N"));
        var primary=await SingleInstanceBroker.Acquire(directory,new(null,null));Assert.NotNull(primary);
        using var cancellation=new CancellationTokenSource();
        var received=new TaskCompletionSource<ActivationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener=primary.Run(request=>{received.TrySetResult(request);return Task.CompletedTask;},cancellation.Token);
        var request=new ActivationRequest(null,@"D:\图库\示例 🖼.jpg");
        try
        {
            Assert.Null(await SingleInstanceBroker.Acquire(directory,request));
            Assert.Equal(request,await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            await using var independent=await SingleInstanceBroker.Acquire(directory+"-other",new(null,null));Assert.NotNull(independent);
        }
        finally{cancellation.Cancel();await listener;await primary.DisposeAsync();}
        await using var replacement=await SingleInstanceBroker.Acquire(directory,new(null,null));Assert.NotNull(replacement);
    }

    [Fact]
    public async Task PreviewAndFirstPageRemainAvailableWhileBackgroundReaderIsOccupied()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-interactive-db",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(3);
        var filter=new FilterSpec{RootId="benchmark"};var initial=await catalog.ReadFirstPage(filter);var item=initial.Items[0];
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background=catalog.Read(_=>{entered.SetResult();return release.Wait(TimeSpan.FromSeconds(10));});
        await entered.Task;
        try
        {
            Assert.Equal(3,(await catalog.ReadFirstPage(filter).WaitAsync(TimeSpan.FromSeconds(2))).Items.Count);
            Assert.NotNull(await catalog.ReadFileProperties("benchmark",item.EntryId,item.Version).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(await catalog.ReadFileProperties("benchmark",item.EntryId,item.Version+1).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally{release.Set();await background;}
    }
}
