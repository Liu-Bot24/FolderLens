using FolderLens.Infrastructure;
using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlaylistPersistenceTests
{
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
