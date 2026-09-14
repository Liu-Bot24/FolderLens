using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScanSchedulingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferredSubtreeCanChangeWithoutStarvingOtherBranches(bool changeFocus)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source");
        for(int i=0;i<20;i++){string folder=Path.Combine(source,"00-large",$"child-{i:D2}");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"photo.jpg"),"image");}
        Directory.CreateDirectory(Path.Combine(source,"07-small","images"));File.WriteAllText(Path.Combine(source,"07-small","images","photo.jpg"),"image");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        var scanner=new DirectoryIndexer(catalog);scanner.Priority.Prefer("00-large");long? lateAt=null;
        var progress=new InlineProgress(p=>
        {
            if(p.Files==1&&changeFocus)scanner.Priority.Prefer(@"07-small\images");
            if(lateAt is not null)return;
            bool found=catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText=@"SELECT EXISTS(SELECT 1 FROM Files WHERE relative_path='07-small\images\photo.jpg')";return (long)cmd.ExecuteScalar()!!=0;}).GetAwaiter().GetResult();
            if(found)lateAt=p.Files;
        });
        var result=await scanner.Scan("root",source,epoch,true,[],progress,CancellationToken.None);
        Assert.Equal("ready",result.State);Assert.Equal(21,result.Files);Assert.NotNull(lateAt);Assert.InRange(lateAt.Value,2,changeFocus?3:6);
        Assert.Throws<ArgumentException>(()=>scanner.Priority.Prefer(@"..\outside"));
    }
    [Fact]
    public async Task LateSiblingReceivesFilesBeforeLargeFirstBranchFinishes()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source");
        for(int i=0;i<30;i++)
        {
            string folder=Path.Combine(source,"00-large",$"child-{i:D2}");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"photo.jpg"),"image");
        }
        Directory.CreateDirectory(Path.Combine(source,"07-small","images"));File.WriteAllText(Path.Combine(source,"07-small","images","photo.jpg"),"image");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        long? firstLateFile=null;
        var progress=new InlineProgress(p=>
        {
            if(firstLateFile is not null)return;
            bool found=catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText=@"SELECT EXISTS(SELECT 1 FROM Files WHERE relative_path='07-small\images\photo.jpg')";return (long)cmd.ExecuteScalar()!!=0;}).GetAwaiter().GetResult();
            if(found)firstLateFile=p.Files;
        });
        var result=await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],progress,CancellationToken.None);
        Assert.Equal("ready",result.State);Assert.Equal(31,result.Files);Assert.NotNull(firstLateFile);Assert.InRange(firstLateFile.Value,1,5);
    }
    private sealed class InlineProgress(Action<ScanProgress> report):IProgress<ScanProgress>{public void Report(ScanProgress value)=>report(value);}
}
