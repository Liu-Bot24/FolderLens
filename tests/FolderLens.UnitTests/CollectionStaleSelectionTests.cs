using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CollectionStaleSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedMissingCannotBeAddedFromOldSelection(bool snapshotSelection)
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(2);
        string tag=(await catalog.CreateCollection("retained selection")).Id;
        var snapshot=await catalog.CreateSnapshot(new(){RootId="benchmark"},1,1);await catalog.RetainSnapshot(snapshot.Id);
        try
        {
            await catalog.ChangeCollectionMembers([tag],["000000000001"],true);
            await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET entry_state='missing',file_version=file_version+1 WHERE entry_id='000000000001'";return cmd.ExecuteNonQuery();});
            Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
            await Assert.ThrowsAsync<IOException>(()=>snapshotSelection
                ?catalog.ChangeCollectionSelection([tag],snapshot.Id,[new(0,1)],true)
                :catalog.ChangeCollectionMembers([tag],["000000000001"],true));
            Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
            // A live, unaffected selection must still be addable.
            Assert.Equal(1,await catalog.ChangeCollectionMembers([tag],["000000000002"],true));
        }
        finally{await catalog.ReleaseSnapshot(snapshot.Id);}
    }

    [Fact]
    public async Task OldSnapshotCannotCollectRenamedFileInItsNewLocation()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(2);
        string tag=(await catalog.CreateCollection("renamed selection")).Id;
        var snapshot=await catalog.CreateSnapshot(new(){RootId="benchmark"},1,1);await catalog.RetainSnapshot(snapshot.Id);
        try
        {
            await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET relative_path='renamed.jpg',path_revision=path_revision+1 WHERE entry_id='000000000001'";return cmd.ExecuteNonQuery();});
            await Assert.ThrowsAsync<IOException>(()=>catalog.ChangeCollectionSelection([tag],snapshot.Id,[new(0,1)],true));
            Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
            var current=await catalog.CreateSnapshot(new(){RootId="benchmark"},1,2);await catalog.RetainSnapshot(current.Id);
            try{Assert.Equal(2,await catalog.ChangeCollectionSelection([tag],current.Id,[new(0,2)],true));}
            finally{await catalog.ReleaseSnapshot(current.Id);}
        }
        finally{await catalog.ReleaseSnapshot(snapshot.Id);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickSelectionRejectsMoveEvenWhenNameIsRestored(bool restoreName)
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(2);
        string tag=(await catalog.CreateCollection("quick")).Id;
        var first=await catalog.ReadFirstPage(new(){RootId="benchmark"});var item=first.Items[0];
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET relative_path=$path,path_revision=path_revision+1 WHERE entry_id=$id";cmd.Parameters.AddWithValue("$id",item.EntryId);cmd.Parameters.AddWithValue("$path",restoreName?item.RelativePath:"renamed.jpg");return cmd.ExecuteNonQuery();});
        await Assert.ThrowsAsync<IOException>(()=>catalog.ChangeCollectionItems([tag],[first.Items[1],item],true));
        Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
        var current=await catalog.ReadFirstPage(new(){RootId="benchmark"});
        Assert.Equal(2,await catalog.ChangeCollectionItems([tag],current.Items,true));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("unknown")]
    public async Task UnavailableRootDoesNotInvalidateUnchangedLocalMembership(string availability)
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(1);
        var first=await catalog.ReadFirstPage(new(){RootId="benchmark"});string tag=(await catalog.CreateCollection("offline")).Id;
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Roots SET availability=$availability WHERE root_id='benchmark'";cmd.Parameters.AddWithValue("$availability",availability);return cmd.ExecuteNonQuery();});
        Assert.Equal(1,await catalog.ChangeCollectionItems([tag],first.Items,true));
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
    }
}
