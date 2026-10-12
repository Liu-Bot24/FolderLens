using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CollectionDirectoryContextTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ColdInteractiveConnectionSupportsPickerAndPreviewBeforeFirstPage(bool pickerFirst)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-context-cold",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source","A"),data=Path.Combine(fixture,"db");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"image.png"),"generated");string collection;
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
            await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            collection=(await catalog.CreateCollection("cold")).Id;
            await catalog.ChangeCollectionItems([collection],(await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items,true);
        }
        await using var cold=new CatalogStore(data);await cold.Initialize();
        async Task CheckPicker()=>Assert.Equal(source,await cold.CollectionSourceDirectory(collection,source));
        async Task CheckPreview()=>Assert.Equal(1,(await cold.PreviewDirectoryRules("collection:"+collection,[new("include","name","equals","A")],collectionId:collection)).VisibleFiles);
        if(pickerFirst){await CheckPicker();await CheckPreview();}else{await CheckPreview();await CheckPicker();}
    }
    [Fact]
    public async Task PartialRefreshCountsEachSourceDirectoryOnceAcrossObservationRoots()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-context-mixed",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source");
        foreach(string path in new[]{"A\\one.png","A\\two.png","B\\other.png"})
        {string full=Path.Combine(source,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllText(full,"generated");}
        await using var catalog=new CatalogStore(Path.Combine(fixture,"db"),new(),Path.Combine(fixture,"playlist.sqlite"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source);await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
        string collection=(await catalog.CreateCollection("mixed")).Id;
        await catalog.ChangeCollectionItems([collection],(await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items,true);
        await using(var batches=catalog.RefreshPlaylistBatches(collection).GetAsyncEnumerator())Assert.True(await batches.MoveNextAsync());
        var preview=await catalog.PreviewDirectoryRules("collection:"+collection,[new("exclude","name","equals","A")],collectionId:collection);
        Assert.Equal(1,preview.VisibleDirectories);Assert.Equal(1,preview.HiddenDirectories);Assert.Equal(1,preview.VisibleFiles);Assert.Equal(2,preview.HiddenFiles);Assert.Single(preview.HiddenExamples);
    }
    [Fact]
    public async Task RulesKeepSourceDirectoryMeaningAfterPlaylistRefreshAndColdReopen()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-collection-context",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");
        foreach(string folder in new[]{"A","A\\child","B"})
        {string parent=Path.Combine(source,folder);Directory.CreateDirectory(parent);File.WriteAllText(Path.Combine(parent,"photo.png"),"generated");}
        string collection;
        async Task Check(CatalogStore catalog)
        {
            var filter=new FilterSpec{RootId="collection:"+collection,CollectionId=collection,Kinds=[],DirectoryRules=[new("include","name","equals","A")]};
            Assert.Equal(2,(await catalog.ReadFirstPage(filter)).Items.Count);
            Assert.Equal(2,(await catalog.CreateSnapshot(filter,1,1)).Count);
            var preview=await catalog.PreviewDirectoryRules(filter.RootId,filter.DirectoryRules,collectionId:collection);
            Assert.Equal(2,preview.VisibleFiles);Assert.Equal(1,preview.HiddenFiles);
            string selectedDirectory=await catalog.CollectionSourceDirectory(collection,Path.Combine(source,"A"));
            Assert.Equal(Path.Combine(source,"A"),selectedDirectory);
            var absolute=filter with{SchemaVersion=2,DirectoryRules=[new("include","sourcePath","equals",selectedDirectory)]};
            Assert.Equal(2,(await catalog.ReadFirstPage(absolute)).Items.Count);
            Assert.Equal(2,(await catalog.CreateSnapshot(absolute,1,2)).Count);
            Assert.Equal(2,(await catalog.PreviewDirectoryRules(absolute.RootId,absolute.DirectoryRules,collectionId:collection)).VisibleFiles);
        }
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("normal",source);
            await new DirectoryIndexer(session.Catalog).Scan("normal",source,epoch,true,[],null,CancellationToken.None);
            collection=(await session.Catalog.CreateCollection("directory context")).Id;
            await session.Catalog.ChangeCollectionItems([collection],(await session.Catalog.ReadFirstPage(new(){RootId="normal",Kinds=[]})).Items,true);
            await Check(session.Catalog);
            await session.Catalog.RefreshPlaylist(collection);await Check(session.Catalog);
        }
        await using var reopened=await BrowsingSessionStorage.Open(data);
        await reopened.Catalog.RefreshPlaylist(collection);await Check(reopened.Catalog);
        Assert.Equal(3,(await reopened.Catalog.ReadCollections()).Single().Count);
    }
    [Fact]
    public void LegacyRelativeCollectionRulesRequireExplicitRootWithoutChangingOriginal()
    {
        var original=new FilterSpec{RootId="collection",CollectionId=Guid.NewGuid().ToString("N"),DirectoryRules=[new("include","path","regex",@"^A(?:\\child)?$")]};
        string before=System.Text.Json.JsonSerializer.Serialize(original);
        Assert.Throws<LegacyCollectionRuleContextException>(()=>FilterSql.Build(original));
        var migrated=original.WithCollectionRuleBase(@"C:\photos");
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(original));Assert.Equal(2,migrated.SchemaVersion);
        Assert.Equal(original.DirectoryRules[0].Pattern,migrated.DirectoryRules[0].Pattern);
        var matcher=new DirectoryRuleSet(migrated.DirectoryRules);
        Assert.True(matcher.IsVisible(@"C:\photos\A",@"C:\photos\A"));
        Assert.True(matcher.IsVisible(@"C:\photos\A\child",@"C:\photos\A\child"));
        Assert.False(matcher.IsVisible(@"D:\photos\A",@"D:\photos\A"));
        var restored=System.Text.Json.JsonSerializer.Deserialize<FilterSpec>(System.Text.Json.JsonSerializer.Serialize(migrated))!;
        restored.Validate();Assert.Equal(migrated.DirectoryRules,restored.DirectoryRules);
    }
    [Theory]
    [InlineData("equals",@"C:\photos\A")]
    [InlineData("contains",@"photos\A")]
    [InlineData("wildcard",@"C:\photos\?")]
    [InlineData("regex",@"^C:\\photos\\A$")]
    public void SourcePathRulesShareBoundariesAndKeepPrecedence(string match,string pattern)
    {
        var rules=new DirectoryRuleSet([new("exclude","sourcePath",match,pattern),new("keep","name","equals","keep")]);
        Assert.False(rules.IsVisible(@"C:\photos\A",@"C:\photos\A"));
        Assert.True(rules.IsVisible(@"C:\other\A",@"C:\other\A"));
        Assert.True(rules.IsVisible(@"C:\photos\A\keep",@"C:\photos\A\keep"));
    }
}
