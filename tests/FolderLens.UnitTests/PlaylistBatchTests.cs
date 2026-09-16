using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlaylistBatchTests
{
    [Fact]
    public async Task FirstVerifiedBatchPrecedesSlowTailAndCancellationRetainsLinks()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-playlist-batch",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(directory,"source"),data=Path.Combine(directory,"data"),collection;
        Directory.CreateDirectory(source);
        for(int i=0;i<130;i++)File.WriteAllText(Path.Combine(source,$"{i:D3}.jpg"),"fixture");
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            collection=(await session.Catalog.CreateCollection("batch")).Id;
            await session.Catalog.ChangeCollectionItems([collection],(await session.Catalog.ReadFirstPage(new(){RootId="root"})).Items,true);
        }
        await using var restored=await BrowsingSessionStorage.Open(data);var catalog=restored.Catalog;
        int parents=0,files=0;
        var slowTail=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation=new CancellationTokenSource();
        catalog.PlaylistProbeOverride=async(path,token)=>
        {
            if(path==source)parents++;
            else if(++files>1){slowTail.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            return ScanPathProbe.Read(path);
        };
        await using var batches=catalog.RefreshPlaylistBatches(collection,cancellation.Token).GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync());Assert.Equal(2,parents);Assert.Equal(1,files);
        Assert.Single((await catalog.ReadFirstPage(new(){RootId="collection:"+collection,CollectionId=collection})).Items);
        var tail=batches.MoveNextAsync().AsTask();await slowTail.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>tail);
        catalog.PlaylistProbeOverride=(_,_)=>Task.FromResult(new ScanDirectoryPacket("offline",[],"fixture"));
        await catalog.RefreshPlaylist(collection);
        int unrestored=0;long after=0;
        while(true){var page=await catalog.ReadUnrestoredPlaylistLinks(collection,after);if(page.Count==0)break;unrestored+=page.Count;after=page[^1].Id;}
        Assert.Equal(129,unrestored);
        parents=files=0;int completedBatches=0;
        catalog.PlaylistProbeOverride=(path,_)=>{if(path==source)parents++;else files++;return Task.FromResult(ScanPathProbe.Read(path));};
        await foreach(var _ in catalog.RefreshPlaylistBatches(collection)){completedBatches++;Assert.Equal(completedBatches*2,parents);}
        Assert.Equal(130,files);Assert.True(parents<files/4,"Parent probes should be shared within each bounded batch.");
    }
    [Fact]
    public async Task ParentReplacementWithSameFileIdentityIsNotRestoredAndBudgetIsExplicit()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-playlist-replace",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(directory,"source"),data=Path.Combine(directory,"data"),path=Path.Combine(source,"a.jpg"),collection;
        Directory.CreateDirectory(source);File.WriteAllText(path,"fixture");
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            collection=(await session.Catalog.CreateCollection("replace")).Id;
            await session.Catalog.ChangeCollectionItems([collection],(await session.Catalog.ReadFirstPage(new(){RootId="root"})).Items,true);
        }
        await using var restored=await BrowsingSessionStorage.Open(data);var catalog=restored.Catalog;bool replaced=false;
        catalog.PlaylistProbeOverride=(value,_)=>
        {
            if(value==path&&!replaced)
            {
                replaced=true;Directory.Move(source,source+"-old");Directory.CreateDirectory(source);
                File.Move(Path.Combine(source+"-old","a.jpg"),path);
            }
            return Task.FromResult(ScanPathProbe.Read(value));
        };
        await catalog.RefreshPlaylist(collection);
        Assert.Empty((await catalog.ReadFirstPage(new(){RootId="collection:"+collection,CollectionId=collection})).Items);
        Assert.Single(await catalog.ReadUnrestoredPlaylistLinks(collection));
        catalog.PlaylistProbeOverride=async(_,token)=>{await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();};
        await Assert.ThrowsAsync<TimeoutException>(async()=>{await foreach(var _ in catalog.RefreshPlaylistBatches(collection,timeBudget:TimeSpan.FromMilliseconds(100))){};});
        Assert.Single(await catalog.ReadUnrestoredPlaylistLinks(collection));
    }
}
