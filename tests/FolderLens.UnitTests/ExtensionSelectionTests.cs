using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ExtensionSelectionTests
{
    [Fact] public async Task SelectedExtensionsWorkBeforeMetadataAndIntersectCategory()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(3);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET extension=CASE relative_path WHEN 'file1.jpg' THEN '.JPG' WHEN 'file2.jpg' THEN '.png' ELSE '' END,format_id=NULL";return cmd.ExecuteNonQuery();});
        var filter=new FilterSpec{RootId="benchmark",FileExtensions=["jpg"]};
        var jpg=await catalog.CreateSnapshot(filter,1,1);Assert.Equal(1,jpg.Count);Assert.Equal(0,jpg.Pending);
        var png=await catalog.CreateSnapshot(filter with{FileExtensions=["png"]},1,2);Assert.Equal(1,png.Count);
        var both=await catalog.CreateSnapshot(filter with{FileExtensions=["jpg","png"]},1,3);Assert.Equal(2,both.Count);
        var no=await catalog.CreateSnapshot(filter with{Extensions=["pdf"]},1,4);Assert.Equal(0,no.Count);
        var none=await catalog.CreateSnapshot(filter with{FileExtensions=[""]},1,5);Assert.Equal(1,none.Count);
    }
    [Fact] public async Task OptionsUseDirectoryAndCategoryButNotCurrentFormatOrMissingMetadata()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(4);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET extension=CASE relative_path WHEN 'file1.jpg' THEN '.JPG' WHEN 'file2.jpg' THEN '.png' WHEN 'file3.jpg' THEN '.PNG' ELSE '.pdf' END,relative_path=CASE relative_path WHEN 'file3.jpg' THEN 'child\\file3.PNG' ELSE relative_path END,kind=CASE relative_path WHEN 'file4.jpg' THEN 'other' ELSE 'image' END,format_id=NULL";return cmd.ExecuteNonQuery();});
        var filter=new FilterSpec{RootId="benchmark",Formats=["jpeg"],FileExtensions=["jpg"],NamePathQuery="no matches",Ranges=new(){["width"]=new(9999,null)}};
        Assert.Equal(new[]{"jpg","png"},(await catalog.ReadFileExtensions(filter)).Values);
        Assert.Equal(new[]{"png"},(await catalog.ReadFileExtensions(filter with{DirectoryScope="child"})).Values);
        Assert.Equal(new[]{"pdf"},(await catalog.ReadFileExtensions(filter with{Kinds=[],Extensions=["pdf"]})).Values);
        Assert.Empty((await catalog.ReadFileExtensions(filter with{RootId="different"})).Values);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET file_attributes=2 WHERE extension='.png'";return cmd.ExecuteNonQuery();});
        Assert.Equal(new[]{"jpg"},(await catalog.ReadFileExtensions(filter with{Recursive=false})).Values);
        Assert.Equal(new[]{"jpg","png"},(await catalog.ReadFileExtensions(filter with{Recursive=false,ShowHidden=true})).Values);
        Assert.Equal("all",FileCategories.FromFilter(new FilterSpec{RootId="benchmark",Kinds=[],FileExtensions=["pdf"]}));
    }
}
