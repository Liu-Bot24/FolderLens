using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CatalogTests
{
    private static string DirectoryPath()=>Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
    [Fact] public async Task RealSqliteSnapshotPreservesOrderAndValuesAfterChanges()
    {
        await using var catalog=new CatalogStore(DirectoryPath());await catalog.Initialize();await catalog.SeedBenchmark(1200);
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        var first=await catalog.ReadPage(handle.Id,0);var deep=await catalog.ReadPage(handle.Id,1100);
        Assert.Equal(1200,handle.Count);Assert.Equal("file1.jpg",first[0].RelativePath);Assert.Equal("file1101.jpg",deep[0].RelativePath);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET logical_bytes=7,file_version=file_version+1";return cmd.ExecuteNonQuery();});
        var after=await catalog.ReadPage(handle.Id,1100);Assert.Equal(deep,after);
        var fresh=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Ranges=new(){["logicalBytes"]=new(7,7)}},1,2);Assert.Equal(1200,fresh.Count);
    }
    [Fact] public async Task UnknownDoesNotBecomeStaticOrZeroAndKnownFalseWins()
    {
        await using var catalog=new CatalogStore(DirectoryPath());await catalog.Initialize();await catalog.SeedBenchmark(3);
        var filter=new FilterSpec{RootId="benchmark",Animation="static"};
        var pending=await catalog.CreateSnapshot(filter,1,1);Assert.Equal(0,pending.Count);Assert.Equal(3,pending.Pending);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO FieldStates(entry_id,field_group,source_version,state) SELECT entry_id,'animation',file_version,'failed' FROM Files";return cmd.ExecuteNonQuery();});
        var failed=await catalog.CreateSnapshot(filter,1,2);Assert.Equal(3,failed.Unresolvable);
        var no=await catalog.CreateSnapshot(filter with {Ranges=new(){["logicalBytes"]=new(999999,null)}},1,3);Assert.Equal(0,no.Pending);Assert.Equal(0,no.Unresolvable);Assert.Equal(0,no.Count);
        var allocation=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Ranges=new(){["allocatedBytes"]=new(0,0)}},1,4);Assert.Equal(0,allocation.Count);Assert.Equal(3,allocation.Pending);
    }
    [Fact] public void RejectsInvalidFilterInsteadOfQuerying()
    {
        Assert.Throws<ArgumentException>(()=>FilterSql.Build(new FilterSpec{RootId="r",Ranges=new(){["width"]=new(200,100)}}));
        Assert.Throws<ArgumentException>(()=>FilterSql.Build(new FilterSpec{RootId="r",Sort=new("name; DROP TABLE Files")}));
        Assert.Throws<ArgumentException>(()=>FilterSql.Build(new FilterSpec{RootId="r",AspectRatio=new(double.NaN,null)}));
    }
    [Fact] public async Task LiteralSqlMetacharactersAreData()
    {
        await using var catalog=new CatalogStore(DirectoryPath());await catalog.Initialize();await catalog.SeedBenchmark(10);
        var none=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",NamePathQuery="' OR 1=1 --"},1,1);Assert.Equal(0,none.Count);
        var one=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",NamePathQuery="\"file2.jpg\""},1,2);Assert.Equal(1,one.Count);
    }
}
