using FolderLens.Infrastructure;
using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlaylistPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnprovenLegacyLinkIsVisibleAndRequiresExplicitRebindOrRemoval(bool rebind)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-unrestored",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");
        Directory.CreateDirectory(source);string path=Path.Combine(source,"photo.jpg");File.WriteAllText(path,"original");string collection;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            collection=(await session.Catalog.CreateCollection("unproven")).Id;
            await session.Catalog.ChangeCollectionItems([collection],(await session.Catalog.ReadFirstPage(new(){RootId="root"})).Items,true);
            await session.Catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE playlist.SavedLinks SET anchor='',directory_identity='',location_key='legacy-unknown',file_identity=NULL";return q.ExecuteNonQuery();});
        }
        File.Move(path,Path.Combine(source,"old.jpg"));File.WriteAllText(path,"replacement");
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            await session.Catalog.RefreshPlaylist(collection);
            var filter=new FilterSpec{RootId="collection:"+collection,CollectionId=collection};
            Assert.Empty((await session.Catalog.ReadFirstPage(filter)).Items);
            var link=Assert.Single(await session.Catalog.ReadUnrestoredPlaylistLinks(collection));Assert.Equal(path,link.Path);
            if(rebind)
            {
                var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
                string worker=Path.Combine(project!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
                await session.Catalog.RebindUnrestoredPlaylistLink(link,scanWorkerExecutable:worker);Assert.Single((await session.Catalog.ReadFirstPage(filter)).Items);
            }
            else await session.Catalog.RemoveUnrestoredPlaylistLink(link);
            Assert.Empty(await session.Catalog.ReadUnrestoredPlaylistLinks(collection));Assert.Equal(rebind?1:0,Assert.Single(await session.Catalog.ReadCollections()).Count);
        }
        await using(var session=await BrowsingSessionStorage.Open(data)){await session.Catalog.RefreshPlaylist(collection);Assert.Equal(rebind?1:0,Assert.Single(await session.Catalog.ReadCollections()).Count);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityHydrationRetainsExactlyOneDurableLinkAndRemovalSurvivesRestart(bool overlappingRoot)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-hydration",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source","child"),data=Path.Combine(fixture,"data");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"photo.jpg"),"fixture");string collection;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            var catalog=session.Catalog;long epoch=await catalog.OpenRoot("root",source);
            await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE DirectoryLocations SET directory_identity=NULL,anchor_locator=NULL,locator_kind='isolated'; DELETE FROM ScanDirectoryIdentities; UPDATE Files SET location_key='unresolved-fixture'";return q.ExecuteNonQuery();});
            collection=(await catalog.CreateCollection("hydrate")).Id;
            await catalog.ChangeCollectionItems([collection],(await catalog.ReadFirstPage(new(){RootId="root"})).Items,true);
            if(overlappingRoot)
            {
                string parent=Path.GetDirectoryName(source)!;long other=await catalog.OpenRoot("other",parent);
                await new DirectoryIndexer(catalog).Scan("other",parent,other,true,[],null,CancellationToken.None);
            }
            var item=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
            var parentProof=ScanPathProbe.Read(source);var fileProof=ScanPathProbe.Read(Path.Combine(source,"photo.jpg"));
            await catalog.Write(c=>
            {
                using var t=c.BeginTransaction();CatalogStore.BindDirectoryLocation(c,t,item.DirectoryId,parentProof.PhysicalIdentity,parentProof.ResolvedLocation);
                using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE Files SET location_key=$key WHERE entry_id=$entry";
                q.Parameters.AddWithValue("$key",CatalogStore.FileLocationKey(source,"photo.jpg",parentProof.CaseMode,parentProof.PhysicalIdentity,fileProof.PhysicalIdentity,item.EntryId));q.Parameters.AddWithValue("$entry",item.EntryId);q.ExecuteNonQuery();t.Commit();return true;
            });
            Assert.Equal(1,Assert.Single(await catalog.ReadCollections()).Count);
        }
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            await session.Catalog.RefreshPlaylist(collection);
            var item=Assert.Single((await session.Catalog.ReadFirstPage(new(){RootId="collection:"+collection,CollectionId=collection})).Items);
            await session.Catalog.ChangeCollectionItems([collection],[item],false);
            Assert.Equal(0,Assert.Single(await session.Catalog.ReadCollections()).Count);
        }
        await using(var session=await BrowsingSessionStorage.Open(data))Assert.Equal(0,Assert.Single(await session.Catalog.ReadCollections()).Count);
    }
    [Theory]
    [InlineData("unknown",false)]
    [InlineData("sensitive",false)]
    [InlineData("unknown",true)]
    public async Task UnknownCaseObservationDoesNotDeletePersistentFavorite(string caseMode,bool replace)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-playlist",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"photo.jpg"),"original");string id;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            id=(await session.Catalog.CreateCollection("case proof")).Id;await session.Catalog.ChangeCollectionItems([id],(await session.Catalog.ReadFirstPage(new(){RootId="root"})).Items,true);
        }
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            if(replace){File.Move(Path.Combine(source,"photo.jpg"),Path.Combine(source,"old.jpg"));File.WriteAllText(Path.Combine(source,"photo.jpg"),"replacement");}
            session.Catalog.PlaylistProbeOverride=(path,_)=>Task.FromResult(ScanPathProbe.Read(path) with{CaseMode=caseMode});
            await session.Catalog.RefreshPlaylist(id);Assert.Equal(replace?0:1,Assert.Single(await session.Catalog.ReadCollections()).Count);
            session.Catalog.PlaylistProbeOverride=null;
            await session.Catalog.RefreshPlaylist(id);
            Assert.Equal(replace?0:1,(await session.Catalog.ReadFirstPage(new(){RootId="collection:"+id,CollectionId=id})).Items.Count);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V1MappingUpgradeRequiresPositiveIdentityProof(bool replace)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-playlist",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"photo.jpg"),"original");string id;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            id=(await session.Catalog.CreateCollection("legacy proof")).Id;await session.Catalog.ChangeCollectionItems([id],(await session.Catalog.ReadFirstPage(new(){RootId="root"})).Items,true);
        }
        using(var c=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"collections","playlists.sqlite"),Pooling=false}.ToString()))
        {
            c.Open();using var cmd=c.CreateCommand();cmd.CommandText="ALTER TABLE SavedLinks DROP COLUMN file_identity;ALTER TABLE SavedLinks DROP COLUMN case_mode;UPDATE PlaylistInfo SET version=1";cmd.ExecuteNonQuery();
        }
        if(replace){File.Move(Path.Combine(source,"photo.jpg"),Path.Combine(source,"old.jpg"));File.WriteAllText(Path.Combine(source,"photo.jpg"),"replacement");}
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            await session.Catalog.RefreshPlaylist(id);
            Assert.Equal(1,Assert.Single(await session.Catalog.ReadCollections()).Count);
            Assert.Equal(replace?0:1,(await session.Catalog.ReadFirstPage(new(){RootId="collection:"+id,CollectionId=id})).Items.Count);
        }
    }
    [Fact]
    public async Task LegacyImportCopiesOnlyMappingsAndDoesNotModifyCatalog()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-playlist",Guid.NewGuid().ToString("N")),data=Path.Combine(fixture,"data"),source=Path.Combine(fixture,"source");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"keep.txt"),"kept");File.WriteAllText(Path.Combine(source,"ignore.txt"),"ignored");
        await using(var legacy=new CatalogStore(Path.Combine(data,"catalog")))
        {
            await legacy.Initialize();long epoch=await legacy.OpenRoot("root",source);await new DirectoryIndexer(legacy).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            string id=(await legacy.CreateCollection("结案")).Id;var files=await legacy.ReadFirstPage(new(){RootId="root",Kinds=[]});await legacy.ChangeCollectionItems([id],[files.Items.Single(i=>i.RelativePath=="keep.txt")],true);
        }
        string legacyPath=Path.Combine(data,"catalog","catalog.sqlite");byte[] hash=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyPath));
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            var saved=Assert.Single(await session.Catalog.ReadCollections());Assert.Equal(1,saved.Count);
            Assert.Empty((await session.Catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items);
            await session.Catalog.RefreshPlaylist(saved.Id);
            Assert.Equal("keep.txt",Assert.Single((await session.Catalog.ReadFirstPage(new(){RootId="collection:"+saved.Id,CollectionId=saved.Id,Kinds=[]})).Items).RelativePath);
            await session.Catalog.DeleteCollection(saved.Id);
        }
        Assert.Equal(hash,System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyPath)));
        await using(var next=await BrowsingSessionStorage.Open(data))Assert.Empty(await next.Catalog.ReadCollections());
    }

    [Fact]
    public async Task RenamedAndReplacedFilesDoNotInheritSavedMembershipOnRestart()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-playlist",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"one.jpg"),"original");string id;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            id=(await session.Catalog.CreateCollection("favorite")).Id;await session.Catalog.ChangeCollectionItems([id],(await session.Catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items,true);
        }
        File.Move(Path.Combine(source,"one.jpg"),Path.Combine(source,"renamed.jpg"));File.WriteAllText(Path.Combine(source,"one.jpg"),"replacement");
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            await session.Catalog.RefreshPlaylist(id);Assert.Equal(0,Assert.Single(await session.Catalog.ReadCollections()).Count);
            Assert.Empty((await session.Catalog.ReadFirstPage(new(){RootId="collection:"+id,CollectionId=id})).Items);
        }
    }
    [Fact]
    public async Task RestartRetainsPlaylistButNotScannedFileHistory()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-playlist",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"saved.jpg"),"saved");File.WriteAllText(Path.Combine(source,"ordinary.jpg"),"ordinary");
        string collectionId;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            var catalog=session.Catalog;long epoch=await catalog.OpenRoot("root",source);
            await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            var page=await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]});
            collectionId=(await catalog.CreateCollection("家具")).Id;
            await catalog.ChangeCollectionItems([collectionId],[page.Items.Single(i=>i.RelativePath=="saved.jpg")],true);
        }
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(data,"runtime")));
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            var catalog=session.Catalog;
            Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
            Assert.Empty((await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items);
            await catalog.RefreshPlaylist(collectionId);
            var saved=await catalog.ReadFirstPage(new(){RootId="collection:"+collectionId,CollectionId=collectionId});
            Assert.Equal("saved.jpg",Assert.Single(saved.Items).RelativePath);
            // A fresh ordinary scan recovers the badge/tag from the mapping, not history.
            long epoch=await catalog.OpenRoot("root",source);
            await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            var excluded=await catalog.ReadFirstPage(new(){RootId="root",ExcludeCollections=[collectionId]});
            Assert.Equal("ordinary.jpg",Assert.Single(excluded.Items).RelativePath);
            File.Delete(Path.Combine(source,"saved.jpg"));await catalog.RefreshPlaylist(collectionId);
            Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
        }
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(data,"runtime")));
        Assert.True(new FileInfo(Path.Combine(data,"collections","playlists.sqlite")).Length<128*1024);
    }
}
