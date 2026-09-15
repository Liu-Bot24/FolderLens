using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlaylistObservationTests
{
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-observation",Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(100,2000)]
    [InlineData(2000,100)]
    public async Task RefreshInvalidatesChangedContentAndChoosesFreshObservation(int before,int after)
    {
        string directory=Temp(),source=Path.Combine(directory,"source"),path=Path.Combine(source,"sample.mp4");Directory.CreateDirectory(source);File.WriteAllText(path,"first");
        await using var catalog=new CatalogStore(Path.Combine(directory,"db"),new(),Path.Combine(directory,"saved.sqlite"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("ordinary",source);await new DirectoryIndexer(catalog).Scan("ordinary",source,epoch,true,[],null,CancellationToken.None);
        var original=Assert.Single((await catalog.ReadFirstPage(new(){RootId="ordinary",Kinds=[]})).Items);
        string collection=(await catalog.CreateCollection("test")).Id;await catalog.ChangeCollectionItems([collection],[original],true);await catalog.RefreshPlaylist(collection);
        var filter=new FilterSpec{RootId="collection:"+collection,CollectionId=collection,Kinds=[],Ranges=new(){["width"]=new(1000,null)}};
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE Files SET display_width=$width,display_height=100,long_edge=MAX($width,100),short_edge=MIN($width,100),pixel_count=$width*100,duration_ms=$width,format_id='mov',source_metadata_version=file_version; INSERT OR REPLACE INTO FieldStates(entry_id,field_group,source_version,state) SELECT entry_id,'imageGeometry',file_version,'ready' FROM Files";q.Parameters.AddWithValue("$width",before);return q.ExecuteNonQuery();});
        File.WriteAllText(path,"changed content with a different length");await catalog.RefreshPlaylist(collection);
        var pending=await catalog.CreateSnapshot(filter,1,1);Assert.Equal(1,pending.Pending);Assert.Equal(0,pending.Count);
        var fresh=Assert.Single((await catalog.ReadFirstPage(filter with{Ranges=[]})).Items);Assert.Equal(new FileInfo(path).Length,fresh.Bytes);
        Assert.True(await catalog.ApplyMediaMetadata(fresh.EntryId,fresh.Version,fresh.SourceRootId!,1,new(after,after,100,"h264",null,30,false,null){FormatId="mov"},"test"));
        var result=await catalog.CreateSnapshot(filter,1,2);Assert.Equal(after>=1000?1:0,result.Count);Assert.Equal(0,result.Pending);
        // A later ordinary scan must become the new authority too, independent of entry-id ordering.
        File.WriteAllText(path,"third");await new DirectoryIndexer(catalog).Scan("ordinary",source,epoch,true,[],null,CancellationToken.None);
        var latest=Assert.Single((await catalog.ReadFirstPage(filter with{Ranges=[]})).Items);Assert.Equal(5,latest.Bytes);
    }
    [Fact]
    public async Task ColdCollectionRestoresFilesystemAttributesWithoutScanningSource()
    {
        string directory=Temp(),source=Path.Combine(directory,"source"),data=Path.Combine(directory,"data"),path=Path.Combine(source,"hidden.jpg");Directory.CreateDirectory(source);File.WriteAllText(path,"generated");string collection;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            collection=(await session.Catalog.CreateCollection("test")).Id;await session.Catalog.ChangeCollectionItems([collection],(await session.Catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items,true);
        }
        File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.Hidden);
        await using var restored=await BrowsingSessionStorage.Open(data);await restored.Catalog.RefreshPlaylist(collection);
        var filter=new FilterSpec{RootId="collection:"+collection,CollectionId=collection,Kinds=[]};
        Assert.Empty((await restored.Catalog.ReadFirstPage(filter)).Items);
        Assert.Single((await restored.Catalog.ReadFirstPage(filter with{ShowHidden=true})).Items);
        var values=await restored.Catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="SELECT ctime_utc_ticks,allocated_bytes FROM Files";using var r=q.ExecuteReader();Assert.True(r.Read());return(r.IsDBNull(0)?0:r.GetInt64(0),r.IsDBNull(1)?-1:r.GetInt64(1));});
        Assert.Equal(File.GetCreationTimeUtc(path).Ticks,values.Item1);Assert.Equal(FileAllocation.Inspect(path).Allocated,values.Item2);
    }
}
