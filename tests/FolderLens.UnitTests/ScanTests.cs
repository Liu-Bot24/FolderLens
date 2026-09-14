using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScanTests
{
    [Fact] public async Task FairScanVisitsSiblingDirectoriesBeforeDescendingFurther()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")),source=Path.Combine(fixture,"source");
        for(int i=0;i<24;i++)
        {
            string contact=Path.Combine(source,$"contact-{i:D2}");Directory.CreateDirectory(Path.Combine(contact,"images"));
            File.WriteAllText(Path.Combine(contact,"manifest.txt"),"manifest");File.WriteAllText(Path.Combine(contact,"images","photo.jpg"),"image candidate");
        }
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        long? filesAtFirstImage=null;
        var progress=new InlineProgress(p=>
        {
            if(filesAtFirstImage is not null||p.Files==0)return;
            long images=catalog.Read(c=>{using var command=c.CreateCommand();command.CommandText="SELECT count(*) FROM Files WHERE kind='image' AND entry_state='present'";return (long)command.ExecuteScalar()!;}).GetAwaiter().GetResult();
            if(images>0)filesAtFirstImage=p.Files;
        });
        var scanner=new DirectoryIndexer(catalog);var result=await scanner.Scan("root",source,epoch,true,[],progress,CancellationToken.None);
        Assert.Equal("ready",result.State);Assert.Equal(48,result.Files);Assert.NotNull(filesAtFirstImage);Assert.Equal(25,filesAtFirstImage.Value);
        var recursive=await catalog.CreateSnapshot(new FilterSpec{RootId="root"},epoch,1);Assert.Equal(24,recursive.Count);
        var direct=await catalog.CreateSnapshot(new FilterSpec{RootId="root",Recursive=false},epoch,2);Assert.Equal(0,direct.Count);
    }
    private sealed class InlineProgress(Action<ScanProgress> report):IProgress<ScanProgress>{public void Report(ScanProgress value)=>report(value);}

    [Fact] public async Task RecursiveScanExclusionReconciliationAndReadOnly()
    {
        string baseDir=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));string source=Path.Combine(baseDir,"source");Directory.CreateDirectory(Path.Combine(source,"foo"));Directory.CreateDirectory(Path.Combine(source,"foobar"));
        File.WriteAllText(Path.Combine(source,"root.txt"),"hello");File.WriteAllText(Path.Combine(source,"foo","a.jpg"),"candidate only");File.WriteAllText(Path.Combine(source,"foobar","b.jpg"),"candidate only");
        await using var catalog=new CatalogStore(Path.Combine(baseDir,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        var indexer=new DirectoryIndexer(catalog);var scan=await Task.Run(()=>indexer.Scan("root",source,epoch,true,[new("foo","skipScan")],null,CancellationToken.None));
        Assert.Equal("ready",scan.State);Assert.Equal(2,scan.Files);Assert.Equal("hello",File.ReadAllText(Path.Combine(source,"root.txt")));
        var images=await catalog.CreateSnapshot(new FilterSpec{RootId="root"},epoch,1);Assert.Equal(1,images.Count);Assert.Equal(@"foobar\b.jpg",(await catalog.ReadPage(images.Id,0))[0].RelativePath);
        File.Delete(Path.Combine(source,"foobar","b.jpg"));await Task.Run(()=>indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None));
        var after=await catalog.CreateSnapshot(new FilterSpec{RootId="root"},epoch,2);Assert.Equal(1,after.Count);Assert.Equal(@"foo\a.jpg",(await catalog.ReadPage(after.Id,0))[0].RelativePath);
        Assert.Equal(1,images.Count);Assert.Equal(@"foobar\b.jpg",(await catalog.ReadPage(images.Id,0))[0].RelativePath);
    }
    [Fact] public async Task OfflineRootDoesNotEraseExistingEntries()
    {
        string baseDir=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));string source=Path.Combine(baseDir,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"a.jpg"),"candidate");
        await using var catalog=new CatalogStore(Path.Combine(baseDir,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);var indexer=new DirectoryIndexer(catalog);
        await Task.Run(()=>indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None));Directory.Move(source,source+"-offline");
        var partial=await Task.Run(()=>indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None));Assert.Equal("partial",partial.State);
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="root"},epoch,1);Assert.Equal(1,handle.Count);
    }
}
