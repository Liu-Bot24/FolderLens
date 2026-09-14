using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CollectionTests
{
    [Fact] public async Task CollectionRevisionChangesOnlyWithCommittedMembership()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(1);string tag=(await catalog.CreateCollection("revision")).Id;
        long initial=await catalog.ReadCollectionRevision();await catalog.ChangeCollectionMembers([tag],["000000000001"],true);
        long added=await catalog.ReadCollectionRevision();Assert.True(added>initial);
        var selected=(await catalog.ReadFirstPage(new(){RootId="benchmark"})).Items;
        Assert.True((await catalog.ReadCollectionFlags(selected)).Single());
        await catalog.ChangeCollectionMembers([tag],["000000000001"],true);Assert.Equal(added,await catalog.ReadCollectionRevision());
        await catalog.Write(c=>{using var t=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DELETE FROM CollectionMembers";cmd.ExecuteNonQuery();t.Rollback();return true;});
        Assert.Equal(added,await catalog.ReadCollectionRevision());
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET entry_state='missing'";return cmd.ExecuteNonQuery();});
        Assert.True(await catalog.ReadCollectionRevision()>added);Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
        Assert.False((await catalog.ReadCollectionFlags(selected)).Single());
    }
    [Fact] public async Task V5UpgradeRemovesReaddedMissingButPreservesLiveMembership()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(2);string tag=(await catalog.CreateCollection("legacy references")).Id;
            await catalog.ChangeCollectionMembers([tag],["000000000001","000000000002"],true);
            await catalog.Write(c=>
            {
                LegacyV5CollectionSchema.Apply(c);
                using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET entry_state='missing' WHERE entry_id='000000000001';INSERT INTO CollectionMembers SELECT $tag,location_key,entry_id,0 FROM Files WHERE entry_id='000000000001'";cmd.Parameters.AddWithValue("$tag",tag);return cmd.ExecuteNonQuery();
            });
            Assert.Equal(2,(await catalog.ReadCollections()).Single().Count);
        }
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
            Assert.Equal(2,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files";return (long)cmd.ExecuteScalar()!;}));
        }
    }
    [Fact] public async Task ExistingV5LocationTriggerRepairDoesNotRewriteDataOrRepeatDdl()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));long repairedVersion;
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(2);
            string tag=(await catalog.CreateCollection("v5 persisted")).Id;await catalog.ChangeCollectionMembers([tag],["000000000001"],true);
            await catalog.Write(c=>
            {
                LegacyV5CollectionSchema.Apply(c);
                using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE RepairWrites(value INTEGER);CREATE TRIGGER TrackRepairWrites AFTER UPDATE OF location_key ON Files BEGIN INSERT INTO RepairWrites VALUES(1);END;";
                return cmd.ExecuteNonQuery();
            });
        }
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();
            Assert.Single(Directory.GetFiles(data,"*.pre-v6-*.bak"));
            Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
            Assert.Equal(6,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version";return (long)cmd.ExecuteScalar()!;}));
            await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET physical_identity=physical_identity";return cmd.ExecuteNonQuery();});
            Assert.Equal(0,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM RepairWrites";return (long)cmd.ExecuteScalar()!;}));
            repairedVersion=await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA schema_version";return (long)cmd.ExecuteScalar()!;});
        }
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();
            Assert.Equal(repairedVersion,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA schema_version";return (long)cmd.ExecuteScalar()!;}));
        }
    }
    [Fact] public async Task ExistingV4TriggerIsRepairedAndCaseChangesRetainMembership()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));string id;
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(2);id=(await catalog.CreateCollection("收藏")).Id;
            await catalog.ChangeCollectionMembers([id],["000000000001"],true);
            await catalog.Write(c=>
            {
                LegacyCollectionSchema.V4(c);return true;
            });
        }
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();
            string repaired=await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT sql FROM sqlite_master WHERE name='Directories_Location_Update'";return (string)cmd.ExecuteScalar()!;});
            Assert.Contains("root_id=NEW.root_id AND directory_id=NEW.directory_id",repaired);
            await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Directories SET case_mode='insensitive' WHERE directory_id='benchmark-dir'";return cmd.ExecuteNonQuery();});
            Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
            var snapshot=await catalog.CreateSnapshot(new(){RootId="benchmark",IncludeCollections=[id]},1,1);
            Assert.Equal("000000000001",(await catalog.ReadPage(snapshot.Id,0)).Single().EntryId);
            Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="benchmark",ExcludeCollections=[id]},1,2)).Count);
        }
    }
    [Fact] public async Task DirectoryCaseChangeUsesScopedIndex()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(2);
        var plan=await catalog.Read(c=>
        {
            using var command=c.CreateCommand();command.CommandText="SELECT sql FROM sqlite_master WHERE name='Directories_Location_Update'";
            string trigger=(string)command.ExecuteScalar()!;
            string update=trigger[(trigger.IndexOf("BEGIN",StringComparison.Ordinal)+5)..trigger.LastIndexOf("END",StringComparison.Ordinal)];
            command.CommandText="EXPLAIN QUERY PLAN "+update.Replace("NEW.case_mode","'insensitive'").Replace("NEW.directory_id","$dir").Replace("NEW.root_id","$root");
            command.Parameters.AddWithValue("$dir","benchmark-dir");command.Parameters.AddWithValue("$root","benchmark");
            using var rows=command.ExecuteReader();var steps=new List<string>();while(rows.Read())steps.Add(rows.GetString(3));return steps;
        });
        Assert.DoesNotContain(plan,step=>step.Contains("SCAN Files"));
        Assert.Contains(plan,step=>step.Contains("IX_Files_Directory")&&step.Contains("root_id=? AND directory_id=?"));
    }
    [Fact] public async Task PropertiesReadPersistedMembershipAndKeepStarUntilLastMembershipRemoved()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string a,b;long version;
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(2);
            var handle=await catalog.CreateSnapshot(new(){RootId="benchmark"},1,1);
            var rows=await catalog.ReadPage(handle.Id,0);version=rows[0].Version;
            Assert.False((await catalog.ReadFileProperties("benchmark","000000000001",version))!.IsCollected);
            a=(await catalog.CreateCollection("家具")).Id;b=(await catalog.CreateCollection("认可")).Id;
            await catalog.ChangeCollectionMembers([a,b],["000000000001"],true);
            Assert.True((await catalog.ReadFileProperties("benchmark","000000000001",version))!.IsCollected);
            Assert.False((await catalog.ReadFileProperties("benchmark","000000000002",rows[1].Version))!.IsCollected);
        }
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();
            Assert.True((await catalog.ReadFileProperties("benchmark","000000000001",version))!.IsCollected);
            await catalog.ChangeCollectionMembers([a],["000000000001"],false);
            Assert.True((await catalog.ReadFileProperties("benchmark","000000000001",version))!.IsCollected);
            await catalog.DeleteCollection(b);
            Assert.False((await catalog.ReadFileProperties("benchmark","000000000001",version))!.IsCollected);
        }
    }
    [Fact] public async Task OverlappingRootsShareTagsAndBulkFailureRollsBack()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(3);await catalog.OpenRoot("child",@"D:\furniture\sub");
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="""
            UPDATE Roots SET display_path='D:\furniture' WHERE root_id='benchmark';
            UPDATE Directories SET case_mode='insensitive' WHERE root_id='benchmark';
            INSERT INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES('child-dir','child','','','','insensitive');
            INSERT INTO ScanDirectoryIdentities VALUES('benchmark-dir','volume:shared-parent'),('child-dir','volume:shared-parent');
            UPDATE Files SET physical_identity='volume:shared-file',relative_path='sub\file1.jpg' WHERE entry_id='000000000001';
            UPDATE Files SET physical_identity='volume:shared-file',root_id='child',directory_id='child-dir',relative_path='FILE1.JPG' WHERE entry_id='000000000002';
            """;return command.ExecuteNonQuery();});
        var collection=await catalog.CreateCollection("Approved");var ids=new[]{collection.Id};
        await catalog.ChangeCollectionMembers(ids,["000000000001"],true);
        Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="child",IncludeCollections=ids},1,1)).Count);
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET physical_identity=NULL WHERE entry_id='000000000001'";return command.ExecuteNonQuery();});
        Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="child",IncludeCollections=ids},1,10)).Count);
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET physical_identity='volume:shared-file' WHERE entry_id='000000000001'";return command.ExecuteNonQuery();});
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="collection:"+collection.Id,CollectionId=collection.Id},1,2)).Count);
        Assert.Equal(0,(await catalog.CreateSnapshot(new(){RootId="child",IncludeCollections=ids,ExcludeCollections=ids},1,3)).Count);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>catalog.ChangeCollectionMembers([collection.Id,Guid.NewGuid().ToString("N")],["000000000003"],true));
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        var handle=await catalog.CreateSnapshot(new(){RootId="benchmark"},1,4);await catalog.RetainSnapshot(handle.Id);
        try
        {
            Assert.Equal(1,await catalog.ChangeCollectionSelection(ids,handle.Id,[new(0,handle.Count)],true));
            Assert.Equal(2,(await catalog.ReadCollections()).Single().Count);
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>catalog.ChangeCollectionSelection([Guid.NewGuid().ToString("N")],handle.Id,[new(0,handle.Count)],true));
            Assert.Equal(2,(await catalog.ReadCollections()).Single().Count);
        }
        finally{await catalog.ReleaseSnapshot(handle.Id);}
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET relative_path='renamed.jpg' WHERE entry_id='000000000002'";return command.ExecuteNonQuery();});
        Assert.Equal(0,(await catalog.CreateSnapshot(new(){RootId="child",IncludeCollections=ids},1,5)).Count);
        Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="child",ExcludeCollections=ids},1,6)).Count);
        await catalog.ChangeCollectionMembers(ids,["000000000002"],true);
        var rule=new DirectoryRule("exclude","name","equals","does-not-exist");
        var scope=new FilterSpec{RootId="collection:"+collection.Id,CollectionId=collection.Id,DirectoryRules=[rule]};
        Assert.Equal(2,(await catalog.CreateSnapshot(scope,1,7)).Count);
        var preview=await catalog.PreviewDirectoryRules(scope.RootId,[rule],collectionId:collection.Id);
        Assert.Equal(2,preview.VisibleFiles);Assert.Equal(0,preview.HiddenFiles);
    }
    [Fact] public async Task CollectionsPersistAcrossRootsAndRemoveConfirmedMissingReferences()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));string id;
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(3);
            var furniture=await catalog.CreateCollection("家具");id=furniture.Id;var excluded=await catalog.CreateCollection("NSFW");
            Assert.Equal(2,await catalog.ChangeCollectionMembers([id],["000000000001","000000000002"],true));
            Assert.Equal(0,await catalog.ChangeCollectionMembers([id],["000000000001"],true));
            await catalog.ChangeCollectionMembers([excluded.Id],["000000000002"],true);
            var scope=new FilterSpec{RootId="collection:"+id,CollectionId=id};
            Assert.Equal(2,(await catalog.CreateSnapshot(scope,1,1)).Count);
            Assert.Equal(1,(await catalog.CreateSnapshot(scope with{ExcludeCollections=[excluded.Id]},1,2)).Count);
            Assert.Equal(2,(await catalog.CreateSnapshot(new(){RootId="benchmark",IncludeCollections=[id]},1,3)).Count);
            Assert.Equal(0,(await catalog.CreateSnapshot(scope with{Kinds=["video"]},1,4)).Count);
            await catalog.OpenRoot("second",@"D:\collection-fixture");
            await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="INSERT INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES('second-dir','second','','','','sensitive');UPDATE Files SET root_id='second',directory_id='second-dir' WHERE entry_id='000000000002';UPDATE Files SET entry_state='missing' WHERE entry_id='000000000001';";return command.ExecuteNonQuery();});
            // Explicitly re-add the moved reference, whose location has changed.
            await catalog.ChangeCollectionMembers([id],["000000000002"],true);
            var result=await catalog.CreateSnapshot(scope,1,5);var page=await catalog.ReadPage(result.Id,0);
            Assert.Single(page);Assert.Contains(page,item=>item.SourceRootId=="second"&&item.SourceRootPath==@"D:\collection-fixture");
            Assert.DoesNotContain(page,item=>item.SourceRootId=="benchmark");
            Assert.Equal(0,(await catalog.CreateSnapshot(new(){RootId="benchmark",IncludeCollections=[id]},1,6)).Count);
            Assert.True(await catalog.RenameCollection(id,"家具素材"));
            await Assert.ThrowsAsync<ArgumentException>(()=>catalog.CreateCollection("家具素材"));
        }
        await using(var reopened=new CatalogStore(data))
        {
            await reopened.Initialize();Assert.Contains(await reopened.ReadCollections(),c=>c.Id==id&&c.Name=="家具素材");
            Assert.True(await reopened.DeleteCollection(id));Assert.DoesNotContain(await reopened.ReadCollections(),c=>c.Id==id);
            Assert.Equal(3,await reopened.Read(c=>{using var command=c.CreateCommand();command.CommandText="SELECT count(*) FROM Files";return (long)command.ExecuteScalar()!;}));
        }
    }
}
