using System.Text.Json;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class BrowseDepthTests
{
    [Fact]
    public async Task LevelsIncludeCurrentDirectoryAndExcludeDeeperFilesAcrossFirstPageAndSnapshot()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        try
        {
            await using var catalog=new CatalogStore(data);await catalog.Initialize();await catalog.SeedBenchmark(7);
            string[] paths=["宇宙.jpg",@"银河系\银河系.mp4",@"银河系\太阳系\太阳系.pptx",@"银河系\太阳系\地球\地球.jpg",@"银河系\太阳系\火星\火星.mp4",@"银河系\太阳系\地球\中国\城市.jpg",@"银河系外\无关.jpg"];
            await catalog.Write(c=>
            {
                using var tx=c.BeginTransaction();
                for(int i=0;i<paths.Length;i++)
                {
                    using var cmd=c.CreateCommand();cmd.Transaction=tx;
                    cmd.CommandText="UPDATE Files SET relative_path=$path,name=$name,extension=$extension,kind=$kind WHERE entry_id=$id";
                    cmd.Parameters.AddWithValue("$path",paths[i]);cmd.Parameters.AddWithValue("$name",Path.GetFileName(paths[i]));
                    cmd.Parameters.AddWithValue("$extension",Path.GetExtension(paths[i]));
                    cmd.Parameters.AddWithValue("$kind",i==1||i==4?"video":i==2?"other":"image");cmd.Parameters.AddWithValue("$id",(i+1).ToString("D12"));cmd.ExecuteNonQuery();
                }
                tx.Commit();return true;
            });
            var filter=new FilterSpec{RootId="benchmark",Kinds=[]};
            foreach(var (levels,expected) in new (int?,int[])[]{(1,[0]),(3,[0,1,2,6]),(4,[0,1,2,3,4,6]),(null,[0,1,2,3,4,5,6])})
            {
                var limited=filter with{MaxFolderLevels=levels};
                var page=await catalog.ReadFirstPage(limited);
                Assert.Equal(expected.Select(i=>paths[i]).Order(),page.Items.Select(i=>i.RelativePath).Order());
                var snapshot=await catalog.CreateSnapshot(limited,1,levels??99);
                Assert.Equal(expected.Length,snapshot.Count);
                Assert.Equal(page.Items.Select(i=>i.EntryId),(await catalog.ReadPage(snapshot.Id,0)).Select(i=>i.EntryId));
            }
            var scoped=filter with{DirectoryScope="银河系/太阳系",MaxFolderLevels=1};
            Assert.Equal(paths[2],Assert.Single((await catalog.ReadFirstPage(scoped)).Items).RelativePath);
            Assert.Equal(new[]{"pptx"},(await catalog.ReadFileExtensions(scoped)).Values);
            Assert.Equal(3,(await catalog.ReadFirstPage(scoped with{MaxFolderLevels=2})).Items.Count);
            Assert.Single((await catalog.ReadFirstPage(scoped with{MaxFolderLevels=4,ScopeDirectFiles=true})).Items);
            // A legacy path-search setting cannot match only the parent directory name.
            Assert.Empty((await catalog.ReadFirstPage(filter with{NamePathQuery="银河系",SearchScope="nameAndPath",Kinds=["image"]})).Items);
            Assert.Equal(paths[3],Assert.Single((await catalog.ReadFirstPage(filter with{NamePathQuery="地球.jpg"})).Items).RelativePath);
            string collection=(await catalog.CreateCollection("跨目录收藏")).Id;
            var selected=(await catalog.ReadFirstPage(filter)).Items.Where(i=>i.RelativePath==paths[3]||i.RelativePath==paths[5]).ToArray();
            await catalog.ChangeCollectionItems([collection],selected,true);
            Assert.Equal(2,(await catalog.ReadFirstPage(filter with{CollectionId=collection,MaxFolderLevels=1})).Items.Count);
        }
        finally{if(Directory.Exists(data))Directory.Delete(data,true);}
    }

    [Fact]
    public void DepthPersistsWithoutChangingScanPolicyAndRejectsInvalidValues()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var original=JsonSerializer.Deserialize<FilterSpec>("{\"rootId\":\"root\"}",options)!;
        Assert.Null(original.MaxFolderLevels);
        var limited=original with{MaxFolderLevels=3};limited.Validate();
        Assert.True(original.HasSameScanPolicy(limited));
        Assert.Equal(3,JsonSerializer.Deserialize<FilterSpec>(JsonSerializer.Serialize(limited,options),options)!.MaxFolderLevels);
        foreach(int value in new[]{0,-1,32768})Assert.Throws<ArgumentException>(()=>(original with{MaxFolderLevels=value}).Validate());
    }
}
