using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextIndexCacheTests
{
    [Fact]
    public async Task OversizedIndexDoesNotPreventReadingOriginalText()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-index-quota",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"source.txt"),cache=Path.Combine(directory,"cache");await File.WriteAllTextAsync(source,"仍然可以阅读\n第二行");
        string key;
        await using(var index=await TextLineIndex.OpenAsync(source,cache)){key=index.VersionKey;await index.BuildToEndAsync();}
        using(var oversized=new FileStream(Path.Combine(cache,key+".flti"),FileMode.Open,FileAccess.Write))oversized.SetLength(TextIndexDirectory.MaximumIndexBytes+32);
        await Assert.ThrowsAsync<IOException>(()=>TextLineIndex.OpenAsync(source,cache));
        using var reader=new BoundedTextReader(source);Assert.Contains("仍然可以阅读",reader.ReadWindow(0,1024).Text);
    }
    [Fact]
    public void IndexCacheEvictsOldInactiveFilesAndKeepsActiveIndex()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-index-quota",Guid.NewGuid().ToString("N"));
        using var cache=new TextIndexDirectory(directory);
        using var active=cache.OpenIndex(0.ToString("X64"));active.WriteByte(42);
        File.WriteAllText(Path.Combine(directory,"unrelated.flti"),"keep");
        for(int i=1;i<=20;i++){using var index=cache.OpenIndex(i.ToString("X64"));index.WriteByte((byte)i);}
        Assert.Equal(8,Directory.GetFiles(directory,"*.flti").Count(path=>Path.GetFileName(path).Length==69));
        active.Position=0;Assert.Equal(42,active.ReadByte());
        Assert.Equal("keep",File.ReadAllText(Path.Combine(directory,"unrelated.flti")));
        Assert.True(File.Exists(Path.Combine(directory,20.ToString("X64")+".flti")));
    }

    [Fact]
    public void FullActiveCacheRejectsNewIndexWithoutDeletingReaders()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-index-quota",Guid.NewGuid().ToString("N"));
        using var cache=new TextIndexDirectory(directory);var readers=new List<FileStream>();
        try
        {
            for(int i=0;i<8;i++)readers.Add(cache.OpenIndex(i.ToString("X64")));
            Assert.Throws<IOException>(()=>cache.OpenIndex(9.ToString("X64")));
            Assert.Equal(8,Directory.GetFiles(directory,"*.flti").Length);
            readers[0].Dispose();using var next=cache.OpenIndex(9.ToString("X64"));
            Assert.Equal(8,Directory.GetFiles(directory,"*.flti").Length);
        }
        finally{foreach(var reader in readers)reader.Dispose();}
    }
}
