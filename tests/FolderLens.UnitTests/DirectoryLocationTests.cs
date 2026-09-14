using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DirectoryLocationTests
{
    [Theory]
    [InlineData("offline",false)]
    [InlineData("inaccessible",false)]
    [InlineData("offline",true)]
    public async Task UnknownMovedTreePersistsSeparateChoicesAndLaterRetiresOnlyOldPosition(string failure,bool ancestor)
    {
        string parent=Fixture(),oldAncestor=Path.Combine(parent,"old"),newAncestor=Path.Combine(parent,"new");
        string before=ancestor?Path.Combine(oldAncestor,"root"):oldAncestor;
        string after=ancestor?Path.Combine(newAncestor,"root"):newAncestor;
        Directory.CreateDirectory(Path.Combine(before,"deep","nested"));File.WriteAllText(Path.Combine(before,"deep","nested","file.txt"),"fixture");
        string data=Fixture(),tag,location;long epoch;
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();var scanner=new DirectoryIndexer(c,Worker());epoch=await c.OpenRoot("old",before);
            await scanner.Scan("old",before,epoch,true,[],null,CancellationToken.None);
            var item=(await c.ReadFirstPage(new(){RootId="old",Kinds=[]})).Items.Single();tag=(await c.CreateCollection("choices")).Id;
            await c.ChangeCollectionItems([tag],[item],true);Directory.Move(oldAncestor,newAncestor);
            scanner.PathProbeOverride=(p,t)=>p==before?Task.FromResult(new ScanDirectoryPacket(failure,[])):Task.Run(()=>ScanPathProbe.Read(p),t);
            epoch=await c.OpenRoot("new",after);await scanner.Scan("new",after,epoch,true,[],null,CancellationToken.None);
            var current=(await c.ReadFirstPage(new(){RootId="new",Kinds=[]})).Items.Single();location=current.DirectoryLocationId!;
            Assert.NotEqual(item.DirectoryLocationId,location);Assert.False((await c.ReadCollectionFlags([current])).Single());
            Assert.Equal(1,await c.ChangeCollectionItems([tag],[current],true));Assert.Equal(2,(await c.ReadCollections()).Single().Count);
            await scanner.Scan("new",after,epoch,true,[],null,CancellationToken.None);
            Assert.Equal(location,(await c.ReadFirstPage(new(){RootId="new",Kinds=[]})).Items.Single().DirectoryLocationId);
        }
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();Assert.Equal(2,(await c.ReadCollections()).Single().Count);
            var scanner=new DirectoryIndexer(c,Worker());await scanner.Scan("new",after,epoch,true,[],null,CancellationToken.None);
            var item=(await c.ReadFirstPage(new(){RootId="new",Kinds=[]})).Items.Single();Assert.Equal(location,item.DirectoryLocationId);
            Assert.True((await c.ReadCollectionFlags([item])).Single());Assert.Equal(1,(await c.ReadCollections()).Single().Count);
            Assert.Equal(0,(await c.CreateSnapshot(new(){RootId="old",Kinds=[],IncludeCollections=[tag]},1,1)).Count);
            // Returning to an observed retired path must not resurrect its old instance.
            Directory.Move(newAncestor,oldAncestor);long back=await c.OpenRoot("old",before);await scanner.Scan("old",before,back,true,[],null,CancellationToken.None);
            Assert.Equal(0,(await c.ReadCollections()).Single().Count);
            Assert.False((await c.ReadCollectionFlags((await c.ReadFirstPage(new(){RootId="old",Kinds=[]})).Items)).Single());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealOverlappingRootsSharePositionInEitherScanOrder(bool childFirst)
    {
        string parent=Fixture(),child=Path.Combine(parent,"child");Directory.CreateDirectory(child);File.WriteAllText(Path.Combine(child,"file.txt"),"fixture");
        await using var c=new CatalogStore(Fixture());await c.Initialize();var scanner=new DirectoryIndexer(c,Worker());
        await c.OpenRoot("parent",parent);await c.OpenRoot("child",child);
        foreach(string id in childFirst?new[]{"child","parent"}:new[]{"parent","child"})await scanner.Scan(id,id=="child"?child:parent,1,true,[],null,CancellationToken.None);
        var a=(await c.ReadFirstPage(new(){RootId="parent",Kinds=[]})).Items.Single();var b=(await c.ReadFirstPage(new(){RootId="child",Kinds=[]})).Items.Single();
        Assert.Equal(a.DirectoryLocationId,b.DirectoryLocationId);string tag=(await c.CreateCollection("shared")).Id;
        Assert.Equal(1,await c.ChangeCollectionItems([tag],[a],true));Assert.Equal(0,await c.ChangeCollectionItems([tag],[b],true));Assert.True((await c.ReadCollectionFlags([b])).Single());
    }

    [Fact]
    public async Task ProvenPositionMergeUnionsTagsAndRejectsStaleSingleAndBulkObservations()
    {
        await using var c=new CatalogStore(Fixture());await c.Initialize();await c.SeedBenchmark(2);
        await c.Write(db=>{using var cmd=db.CreateCommand();cmd.CommandText="""
            INSERT INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES('second','benchmark','second','second','second','sensitive');
            INSERT INTO ScanDirectoryIdentities VALUES('benchmark-dir','physical-parent'),('second','physical-parent');
            UPDATE Files SET physical_identity='physical-file',name='same.jpg',relative_path='same.jpg' WHERE entry_id='000000000001';
            UPDATE Files SET directory_id='second',physical_identity='physical-file',name='same.jpg',relative_path='second\same.jpg' WHERE entry_id='000000000002';
            """;return cmd.ExecuteNonQuery();});
        string shared=(await c.CreateCollection("shared")).Id,onlyNew=(await c.CreateCollection("only new")).Id;
        var items=(await c.ReadFirstPage(new(){RootId="benchmark"})).Items;Assert.Equal(2,await c.ChangeCollectionItems([shared],items,true));
        await c.ChangeCollectionItems([onlyNew],[items.Single(i=>i.DirectoryId=="second")],true);
        var snapshot=await c.CreateSnapshot(new(){RootId="benchmark"},1,1);await c.RetainSnapshot(snapshot.Id);
        string bulk=(await c.CreateCollection("bulk both positions")).Id;
        Assert.Equal(2,await c.ChangeCollectionSelection([bulk],snapshot.Id,[new(0,2)],true));
        await c.Write(db=>{using var t=db.BeginTransaction();CatalogStore.BindDirectoryLocation(db,t,"benchmark-dir","physical-parent","resolved-position");CatalogStore.BindDirectoryLocation(db,t,"second","physical-parent","resolved-position");t.Commit();return true;});
        Assert.All(await c.ReadCollections(),tag=>Assert.Equal(1,tag.Count));
        await Assert.ThrowsAsync<IOException>(()=>c.ChangeCollectionItems([shared],items,true));
        await Assert.ThrowsAsync<IOException>(()=>c.ChangeCollectionSelection([shared],snapshot.Id,[new(0,2)],true));
        await c.ReleaseSnapshot(snapshot.Id);
        var fresh=(await c.ReadFirstPage(new(){RootId="benchmark"})).Items;Assert.All(await c.ReadCollectionFlags(fresh),Assert.True);
        Assert.Equal(0,await c.ChangeCollectionItems([shared],fresh,true));
    }

    [Fact]
    public async Task MissingAccessNamespacePreservesOldMembership()
    {
        string parent=Fixture(),before=Path.Combine(parent,"old"),after=Path.Combine(parent,"new");Directory.CreateDirectory(before);File.WriteAllText(Path.Combine(before,"file.txt"),"fixture");
        await using var c=new CatalogStore(Fixture());await c.Initialize();var scanner=new DirectoryIndexer(c,Worker());await c.OpenRoot("old",before);await scanner.Scan("old",before,1,true,[],null,CancellationToken.None);
        string tag=(await c.CreateCollection("offline route")).Id;await c.ChangeCollectionItems([tag],(await c.ReadFirstPage(new(){RootId="old",Kinds=[]})).Items,true);Directory.Move(before,after);
        // Simulate loss of the namespace, not merely a missing directory entry.
        scanner.PathProbeOverride=(p,t)=>p==before||before.StartsWith(p.TrimEnd('\\')+"\\",StringComparison.Ordinal)?Task.FromResult(new ScanDirectoryPacket("missing",[])):Task.Run(()=>ScanPathProbe.Read(p),t);
        await c.OpenRoot("new",after);await scanner.Scan("new",after,1,true,[],null,CancellationToken.None);
        Assert.Equal(1,(await c.ReadCollections()).Single().Count);Assert.False((await c.ReadCollectionFlags((await c.ReadFirstPage(new(){RootId="new",Kinds=[]})).Items)).Single());
    }
    private static string Fixture(){string path=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    [Fact]
    public async Task ProbeWithChangedBindingCannotRetireOldPosition()
    {
        string parent=Fixture(),before=Path.Combine(parent,"old"),after=Path.Combine(parent,"new");Directory.CreateDirectory(before);File.WriteAllText(Path.Combine(before,"file.txt"),"fixture");
        await using var c=new CatalogStore(Fixture());await c.Initialize();var scanner=new DirectoryIndexer(c,Worker());await c.OpenRoot("old",before);await scanner.Scan("old",before,1,true,[],null,CancellationToken.None);
        var old=(await c.ReadFirstPage(new(){RootId="old",Kinds=[]})).Items.Single();string tag=(await c.CreateCollection("unchanged evidence")).Id;await c.ChangeCollectionItems([tag],[old],true);Directory.Move(before,after);
        bool advanced=false;
        scanner.PathProbeOverride=async(p,t)=>
        {
            if(p==after&&!advanced){advanced=true;await c.Write(db=>{using var cmd=db.CreateCommand();cmd.CommandText="UPDATE DirectoryLocationBindings SET binding_revision=binding_revision+1 WHERE directory_id=$id";cmd.Parameters.AddWithValue("$id",old.DirectoryId);return cmd.ExecuteNonQuery();},t);}
            return await Task.Run(()=>ScanPathProbe.Read(p),t);
        };
        await c.OpenRoot("new",after);await scanner.Scan("new",after,1,true,[],null,CancellationToken.None);
        Assert.True(advanced);Assert.Equal(1,(await c.ReadCollections()).Single().Count);
        Assert.False((await c.ReadCollectionFlags((await c.ReadFirstPage(new(){RootId="new",Kinds=[]})).Items)).Single());
    }

    [Fact]
    public async Task NonemptyV6MigrationKeepsAnchorsAndKeysAndCancellationRollsBack()
    {
        string data=Fixture();string tag;string[] keys;
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();await c.SeedBenchmark(1000);tag=(await c.CreateCollection("migration anchor")).Id;
            await c.ChangeCollectionMembers([tag],["000000000001"],true);
            keys=await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="SELECT location_key FROM Files ORDER BY entry_id";using var rows=cmd.ExecuteReader();var list=new List<string>();while(rows.Read())list.Add(rows.GetString(0));return list.ToArray();});
            await c.Write(db=>
            {
                LegacyV5CollectionSchema.Apply(db);
                using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE CollectionIdentityHistory(entry_id TEXT PRIMARY KEY REFERENCES Files(entry_id) ON DELETE CASCADE,physical_identity TEXT NOT NULL) STRICT;UPDATE SchemaInfo SET schema_version=6;PRAGMA user_version=6;CREATE TABLE MigrationKeyWrites(value INTEGER);CREATE TRIGGER TrackPositionMigration AFTER UPDATE OF location_key ON Files BEGIN INSERT INTO MigrationKeyWrites VALUES(1);END;";return cmd.ExecuteNonQuery();
            });
        }
        using var cancel=new CancellationTokenSource();
        CatalogStore.OperationMigrationMeasured=(phase,ms)=>{if(phase=="directoryLocationPrepared")cancel.Cancel();};
        try{await using var c=new CatalogStore(data);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>c.Initialize(cancel.Token));}
        finally{CatalogStore.OperationMigrationMeasured=null;}
        using(var db=DatabaseExecutor.Open(Path.Combine(data,"catalog.sqlite")))
        {using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";Assert.Equal(6L,cmd.ExecuteScalar());cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE name='DirectoryLocations'";Assert.Equal(0L,cmd.ExecuteScalar());cmd.CommandText="SELECT count(*) FROM CollectionMembers";Assert.Equal(1L,cmd.ExecuteScalar());}
        string location;
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();Assert.Equal(1,(await c.ReadCollections()).Single().Count);
            location=(await c.ReadFirstPage(new(){RootId="benchmark"})).Items.First().DirectoryLocationId!;
            Assert.Equal(0,await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM MigrationKeyWrites";return (long)cmd.ExecuteScalar()!;}));
            Assert.Equal(keys,await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="SELECT location_key FROM Files ORDER BY entry_id";using var rows=cmd.ExecuteReader();var list=new List<string>();while(rows.Read())list.Add(rows.GetString(0));return list.ToArray();}));
        }
        await using(var c=new CatalogStore(data)){await c.Initialize();Assert.Equal(location,(await c.ReadFirstPage(new(){RootId="benchmark"})).Items.First().DirectoryLocationId);}
        Assert.NotEmpty(Directory.GetFiles(data,"*.pre-v7-*.bak"));
    }
    private static string Worker(){var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;return Path.Combine(root!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");}
}
