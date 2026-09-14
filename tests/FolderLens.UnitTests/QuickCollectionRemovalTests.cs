using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class QuickCollectionRemovalTests
{
    [Fact] public async Task RemovingStarClearsAllItsCollectionsAndKeepsOtherFilesAndFolders()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        try
        {
            await using var catalog=new CatalogStore(data);await catalog.Initialize();await catalog.SeedBenchmark(2);
            var items=(await catalog.ReadFirstPage(new(){RootId="benchmark"})).Items;
            // More than the ordinary 64-target add limit: uncollect is one atomic
            // removal of this position, not a partially completed loop over folders.
            for(int i=0;i<65;i++){string id=(await catalog.CreateCollection("folder "+i)).Id;await catalog.ChangeCollectionItems([id],items,true);}
            Assert.All(await catalog.ReadCollectionFlags(items),Assert.True);
            Assert.Equal(65,await catalog.RemoveItemFromCollections(items[0]));
            Assert.Equal(new[]{false,true},await catalog.ReadCollectionFlags(items));
            Assert.All(await catalog.ReadCollections(),folder=>Assert.Equal(1,folder.Count));
            Assert.Equal(0,await catalog.RemoveItemFromCollections(items[0]));
            string target=(await catalog.ReadCollections())[0].Id;
            Assert.Equal(1,await catalog.ChangeCollectionItems([target],[items[0]],true));
            Assert.True((await catalog.ReadCollectionFlags([items[0]])).Single());
        }
        finally{if(Directory.Exists(data))Directory.Delete(data,true);}
    }
    [Fact] public async Task StaleOrCancelledRemovalCannotClearCurrentMembership()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        try
        {
            await using var catalog=new CatalogStore(data);await catalog.Initialize();await catalog.SeedBenchmark(1);
            var item=(await catalog.ReadFirstPage(new(){RootId="benchmark"})).Items.Single();string id=(await catalog.CreateCollection("keep")).Id;
            await catalog.ChangeCollectionItems([id],[item],true);
            await Assert.ThrowsAsync<IOException>(()=>catalog.RemoveItemFromCollections(item with{Version=item.Version+1}));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.RemoveItemFromCollections(item,new CancellationToken(true)));
            Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
            Assert.True((await catalog.ReadCollectionFlags([item])).Single());
        }
        finally{if(Directory.Exists(data))Directory.Delete(data,true);}
    }
}
