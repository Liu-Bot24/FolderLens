using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MultipageFileTests
{
    [Fact]
    public async Task ScannedPagesAreFilesWhileSingleImagesAnimationsAndRawRemainImages()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-pages",Guid.NewGuid().ToString("N"));
        await using var catalog=new CatalogStore(directory);await catalog.Initialize();await catalog.SeedBenchmark(4);
        for(int i=1;i<=4;i++)
            await catalog.ApplyImageMetadata(i.ToString("D12"),1,"benchmark",1,64,48,i==3?"gif":"tiff",i==4,i==3,"fixture",pageCount:i==1?1:2);
        var all=await catalog.ReadFirstPage(new FilterSpec{RootId="benchmark",Kinds=[]});
        Assert.Equal("other",all.Items.Single(row=>row.EntryId=="000000000002").Kind);
        Assert.Equal(3,(await catalog.ReadFirstPage(new FilterSpec{RootId="benchmark",Kinds=["image"]})).Items.Count);
        Assert.Equal(2,(await catalog.ReadFileProperties("benchmark","000000000002",1))!.PageCount);
        Assert.Equal(1,(await catalog.ReadFileProperties("benchmark","000000000003",1))!.PageCount);
        Assert.False(await catalog.ApplyImageMetadata("000000000002",99,"benchmark",1,1,1,"png",false,false,"stale"));
        Assert.Equal("other",(await catalog.ReadFileProperties("benchmark","000000000002",1))!.Kind);
    }

    [Fact]
    public async Task ExistingVerifiedPagesAreMigratedWithRecoverableBackup()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-pages",Guid.NewGuid().ToString("N"));
        await using(var catalog=new CatalogStore(directory))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(2);
            await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET page_count=2,is_raw=0,is_animated=0 WHERE entry_id='000000000001'; UPDATE SchemaInfo SET schema_version=2; PRAGMA user_version=2;";return cmd.ExecuteNonQuery();});
        }
        await using(var catalog=new CatalogStore(directory))
        {
            await catalog.Initialize();
            Assert.Equal("other",(await catalog.ReadFileProperties("benchmark","000000000001",1))!.Kind);
            Assert.Equal("image",(await catalog.ReadFileProperties("benchmark","000000000002",1))!.Kind);
            Assert.Single(Directory.GetFiles(directory,"catalog.sqlite.pre-v3-*.bak"));
        }
    }
}
