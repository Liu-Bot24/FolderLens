using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class FolderGroupingTests
{
    [Fact]
    public async Task DirectoryScopeUsesPathBoundariesAndIndependentDirectFileMode()
    {
        await using var catalog=await Fixture();
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Directories SET relative_path='A%_' WHERE directory_id='B'; UPDATE Files SET relative_path='A%_\\file6.jpg' WHERE directory_id='B';";return cmd.ExecuteNonQuery();});
        var scope=new FilterSpec{RootId="benchmark",DirectoryScope="A",Sort=new("logicalBytes","desc")};
        var handle=await catalog.CreateSnapshot(scope,1,1);
        Assert.Equal(new long[]{50,30,10,2},(await catalog.ReadPage(handle.Id,0)).Select(r=>r.Bytes));
        Assert.Equal(4,(await catalog.ReadFirstPage(scope)).Items.Count);
        var direct=scope with{ScopeDirectFiles=true};Assert.True(direct.Recursive);
        Assert.Equal(@"A\file2.jpg",Assert.Single((await catalog.ReadFirstPage(direct)).Items).RelativePath);
        Assert.Equal(2,(await catalog.ReadFirstPage(scope with{DirectoryScope="A/X"})).Items.Count);
        Assert.Equal(@"A%_\file6.jpg",Assert.Single((await catalog.ReadFirstPage(scope with{DirectoryScope="A%_"})).Items).RelativePath);
        Assert.Equal("file1.jpg",Assert.Single((await catalog.ReadFirstPage(scope with{DirectoryScope="",ScopeDirectFiles=true})).Items).RelativePath);
        var absent=await catalog.CreateSnapshot(scope with{DirectoryScope="missing",Grouping=new(true)},1,2);Assert.Equal(0,absent.Count);
    }

    [Fact]
    public async Task GroupLevelsStartAtBrowseScopeAndRetainFullRelativePaths()
    {
        await using var catalog=await Fixture();
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="""
            INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode) VALUES('Z','benchmark','X','Z','A\X\Z','A\X\Z','sensitive');
            UPDATE Files SET directory_id='Z',relative_path='A\X\Z\file4.jpg' WHERE entry_id='000000000004';
            """;return cmd.ExecuteNonQuery();});
        var filter=new FilterSpec{RootId="benchmark",DirectoryScope="A",Grouping=new(true,"first"),Sort=new("logicalBytes","desc")};
        var first=await catalog.CreateSnapshot(filter,1,1);Assert.Equal(4,first.Count);
        Assert.Equal(new long[]{10,50,30,2},(await catalog.ReadPage(first.Id,0)).Select(r=>r.Bytes));
        Assert.Equal(new[]{"A",@"A\Y",@"A\X"},(await catalog.ReadGroups(first.Id)).Select(g=>g.RelativePath));
        var all=await catalog.CreateSnapshot(filter with{Grouping=new(true)},1,2);
        Assert.Equal(new long[]{10,50,2,30},(await catalog.ReadPage(all.Id,0)).Select(r=>r.Bytes));
        Assert.Equal(new[]{"A",@"A\Y",@"A\X",@"A\X\Z"},(await catalog.ReadGroups(all.Id)).Select(g=>g.RelativePath));
        Assert.Equal(10,(await catalog.ReadGroups(all.Id))[0].Bytes);
    }

    [Fact]
    public async Task NonBmpDirectoryNamesUseSqliteCharacterBoundariesForScopeAndExclusion()
    {
        await using var catalog=await Fixture();
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Directories SET relative_path='💬群' WHERE directory_id='B'; UPDATE Files SET relative_path='💬群\\file6.jpg' WHERE directory_id='B';";return cmd.ExecuteNonQuery();});
        var filter=new FilterSpec{RootId="benchmark",DirectoryScope="💬群",ScopeDirectFiles=true,Grouping=new(true)};
        Assert.Equal(@"💬群\file6.jpg",Assert.Single((await catalog.ReadFirstPage(filter)).Items).RelativePath);
        Assert.Equal(1,(await catalog.CreateSnapshot(filter,1,1)).Count);
        Assert.Equal(5,(await catalog.ReadFirstPage(filter with{DirectoryScope="",ScopeDirectFiles=false,Grouping=new(),Exclusions=[new("💬群","skipScan")]})).Items.Count);
    }

    [Fact]
    public void ScopeValidationAndOldSavedViewDefaults()
    {
        var options=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var old=System.Text.Json.JsonSerializer.Deserialize<FilterSpec>("{\"rootId\":\"root\"}",options)!;
        Assert.Equal("",old.DirectoryScope);Assert.False(old.ScopeDirectFiles);
        foreach(string path in new[]{@"C:\pics",@"..\pics",@"A\..\B",@"\A",@"A\\B","A/./B","A\nB","A:stream"})
            Assert.Throws<ArgumentException>(()=>(old with{DirectoryScope=path}).Validate());
        var current=old with{DirectoryScope=@"A\B",ScopeDirectFiles=true};current.Validate();
        var restored=System.Text.Json.JsonSerializer.Deserialize<FilterSpec>(System.Text.Json.JsonSerializer.Serialize(current,options),options)!;
        Assert.Equal(current.DirectoryScope,restored.DirectoryScope);Assert.True(restored.ScopeDirectFiles);Assert.True(restored.Recursive);
    }

    [Fact]
    public void ViewChangesReuseScanButScanPolicyChangesDoNot()
    {
        var source=new FilterSpec{RootId="root",Exclusions=[new("cache/images","skipScan"),new("hidden","hideView")]};
        Assert.True(source.HasSameScanPolicy(source with{DirectoryScope="A",ScopeDirectFiles=true,Kinds=["video"],Grouping=new(true)}));
        Assert.True(source.HasSameScanPolicy(source with{Exclusions=[new(@"cache\images","skipScan"),new("other","hideView")]}));
        Assert.False(source.HasSameScanPolicy(source with{Recursive=false}));
        Assert.False(source.HasSameScanPolicy(source with{Exclusions=[]}));
        Assert.False(source.HasSameScanPolicy(source with{Exclusions=[new("other","skipScan")]}));
    }

    [Fact]
    public async Task RecursiveAndFirstLevelHaveDistinctStableOrdersWithoutDuplicates()
    {
        await using var catalog=await Fixture();
        var filter=new FilterSpec{RootId="benchmark",Sort=new("logicalBytes","desc"),Grouping=new(true)};
        var recursive=await catalog.CreateSnapshot(filter,1,1);
        var groups=await catalog.ReadGroups(recursive.Id);
        Assert.Equal(new long[]{0,1,2,3,5},groups.Select(g=>g.Start));
        Assert.Equal(new long[]{1,1,1,2,1},groups.Select(g=>g.Count));
        Assert.Equal(1092,groups[1].Bytes);Assert.Equal(4,groups[1].MatchCount);
        Assert.Equal(groups[3],(await catalog.ReadPage(recursive.Id,4,1))[0].Group);
        Assert.Equal(new[]{"file1.jpg","A\\file2.jpg","A\\Y\\file5.jpg","A\\X\\file4.jpg","A\\X\\file3.jpg","B\\file6.jpg"},(await catalog.ReadPage(recursive.Id,0)).Select(item=>item.RelativePath));
        Assert.Equal((await catalog.ReadPage(recursive.Id,0)).Select(i=>i.EntryId),(await catalog.ReadFirstPage(filter)).Items.Select(i=>i.EntryId));
        var first=await catalog.CreateSnapshot(filter with{Grouping=new(true,"first")},1,2);
        Assert.Equal(new[]{"file1.jpg","A\\Y\\file5.jpg","A\\X\\file4.jpg","A\\file2.jpg","A\\X\\file3.jpg","B\\file6.jpg"},(await catalog.ReadPage(first.Id,0)).Select(i=>i.RelativePath));
        var plain=await catalog.CreateSnapshot(filter with{Grouping=new()},1,3);
        Assert.Equal(new long[]{200,50,30,10,5,2},(await catalog.ReadPage(plain.Id,0)).Select(i=>i.Bytes));
        Assert.Equal(6,recursive.Count);Assert.Equal(6,(await catalog.ReadPage(recursive.Id,0)).Select(i=>i.EntryId).Distinct().Count());
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET logical_bytes=1";return cmd.ExecuteNonQuery();});
        Assert.Equal(groups,await catalog.ReadGroups(recursive.Id));
        Assert.Equal(new long[]{5,10,50,30,2,200},(await catalog.ReadPage(recursive.Id,0)).Select(i=>i.Bytes));
    }

    [Fact]
    public async Task FilteredCapacityAndExclusionsRespectTheirIndependentScopes()
    {
        await using var catalog=await Fixture();
        var filter=new FilterSpec{RootId="benchmark",Sort=new("logicalBytes","desc"),Grouping=new(true,CapacityScope:"matches")};
        var page=await catalog.ReadFirstPage(filter);
        Assert.Equal("B\\file6.jpg",page.Items[1].RelativePath);
        // Hiding a video's branch does not remove it from the whole-scanned capacity.
        var hidden=filter with{Grouping=new(true),Exclusions=[new("A\\hidden","hideView")]};
        Assert.Equal("A\\file2.jpg",(await catalog.ReadFirstPage(hidden)).Items[1].RelativePath);
        var skipped=hidden with{Exclusions=[new("A\\hidden","skipScan")]};
        Assert.Equal("B\\file6.jpg",(await catalog.ReadFirstPage(skipped)).Items[1].RelativePath);
        var onlyRoot=filter with{Recursive=false};
        Assert.Equal("file1.jpg",Assert.Single((await catalog.ReadFirstPage(onlyRoot)).Items).RelativePath);
    }

    [Fact]
    public async Task CancellationDoesNotPublishReadyAndInvalidOptionsAreRejected()
    {
        await using var catalog=await Fixture();
        var filter=new FilterSpec{RootId="benchmark",Grouping=new(true)};
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.CreateSnapshot(filter,1,1,stop.Token));
        Assert.Throws<ArgumentException>(()=>(filter with{Grouping=new(true,"flatten")}).Validate());
        Assert.Throws<ArgumentException>(()=>(filter with{Grouping=new(true,Field:"durationMs")}).Validate());
        Assert.Equal(6,(await catalog.CreateSnapshot(filter,1,2)).Count);
    }

    private static async Task<CatalogStore> Fixture()
    {
        var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-group",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(7);
        await catalog.Write(c=>
        {
            using var transaction=c.BeginTransaction();
            foreach(var (id,parent,path) in new[]{("A","benchmark-dir","A"),("B","benchmark-dir","B"),("X","A",@"A\X"),("Y","A",@"A\Y"),("hidden","A",@"A\hidden")})
            {
                using var cmd=c.CreateCommand();cmd.Transaction=transaction;cmd.CommandText="INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode) VALUES($id,'benchmark',$parent,$id,$path,$path,'sensitive')";
                cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$parent",parent);cmd.Parameters.AddWithValue("$path",path);cmd.ExecuteNonQuery();
            }
            var files=new[]{("benchmark-dir","",5),("A",@"A\",10),("X",@"A\X\",2),("X",@"A\X\",30),("Y",@"A\Y\",50),("B",@"B\",200),("hidden",@"A\hidden\",1000)};
            for(int index=0;index<files.Length;index++)
            {
                var (directory,path,bytes)=files[index];using var cmd=c.CreateCommand();cmd.Transaction=transaction;
                cmd.CommandText="UPDATE Files SET directory_id=$dir,relative_path=$path,logical_bytes=$bytes,kind=$kind WHERE entry_id=$id";
                cmd.Parameters.AddWithValue("$dir",directory);cmd.Parameters.AddWithValue("$path",path+$"file{index+1}.jpg");cmd.Parameters.AddWithValue("$bytes",bytes);cmd.Parameters.AddWithValue("$kind",index==6?"video":"image");cmd.Parameters.AddWithValue("$id",(index+1).ToString("D12"));cmd.ExecuteNonQuery();
            }
            transaction.Commit();return true;
        });return catalog;
    }
}
