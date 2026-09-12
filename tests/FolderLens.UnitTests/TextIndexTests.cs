using System.Security.Cryptography;
using System.Text;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextIndexTests
{
    private static (string Path,string Cache,Encoding Encoding) Fixture(string content,string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var encoding=Encoding.GetEncoding(name);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"text.txt");File.WriteAllBytes(path,[..encoding.GetPreamble(),..encoding.GetBytes(content)]);
        return(path,Path.Combine(directory,"line-index"),encoding);
    }
    [Theory][InlineData("utf-8")][InlineData("utf-16")][InlineData("utf-16BE")][InlineData("gb18030")]
    public async Task DiskIndexFindsExactLinesReopensAndNeverWritesSource(string encoding)
    {
        string content=new string('a',262143)+"\r\n"+string.Join("\r\n",Enumerable.Range(2,7000).Select(n=>$"行{n}😀"))+"\r\n";
        var f=Fixture(content,encoding);byte[] before=SHA256.HashData(File.ReadAllBytes(f.Path));DateTime write=File.GetLastWriteTimeUtc(f.Path);
        long expected=f.Encoding.GetPreamble().Length+f.Encoding.GetByteCount(content[..content.IndexOf("行5000😀",StringComparison.Ordinal)]);
        await using(var index=await TextLineIndex.OpenAsync(f.Path,f.Cache,encoding,fileVersion:9))
        {
            var position=await index.EnsureLineAsync(5000);Assert.Equal(new TextPosition(expected,5000,1),position);
            var page=await index.ReadLineWindowAsync(5000,257);Assert.StartsWith("行5000😀\r\n",page!.Text);
            Assert.Equal(position,await index.GetPositionAsync(expected));
            await index.BuildToEndAsync();Assert.Equal(7002,index.Progress.TotalLines);Assert.True(index.Progress.Checkpoints>1);
            Assert.Null(await index.EnsureLineAsync((long)int.MaxValue+5));
            Assert.Equal(index.Progress.Length,(await index.GetPositionAsync(long.MaxValue)).ByteOffset);
        }
        await using(var reopened=await TextLineIndex.OpenAsync(f.Path,f.Cache,encoding,fileVersion:9))
        {Assert.True(reopened.Progress.Complete);Assert.False(reopened.RebuiltCorruptCache);Assert.Equal(expected,(await reopened.EnsureLineAsync(5000))!.ByteOffset);}
        Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(f.Path)));Assert.Equal(write,File.GetLastWriteTimeUtc(f.Path));
    }
    [Fact] public async Task CancellationAndReplacementInvalidateLineRequests()
    {
        var f=Fixture("one\r\ntwo\r\n","utf-8");
        await using var index=await TextLineIndex.OpenAsync(f.Path,f.Cache);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>index.EnsureLineAsync(9999999999,cancellation:cancel.Token));
        DateTime original=File.GetLastWriteTimeUtc(f.Path);byte[] bytes=File.ReadAllBytes(f.Path);
        File.Move(f.Path,f.Path+".old");File.WriteAllBytes(f.Path,bytes);File.SetLastWriteTimeUtc(f.Path,original);
        await Assert.ThrowsAsync<IOException>(()=>index.EnsureLineAsync(1));
    }
    [Fact] public async Task DamagedDerivedIndexRebuildsAndEncodingKeysAreDistinct()
    {
        var f=Fixture("first\nsecond\n","utf-8");string key;
        await using(var index=await TextLineIndex.OpenAsync(f.Path,f.Cache)){await index.BuildToEndAsync();key=index.VersionKey;}
        string file=Path.Combine(f.Cache,key+".flti");byte[] bad=File.ReadAllBytes(file);bad[140]^=0x40;File.WriteAllBytes(file,bad);
        await using(var index=await TextLineIndex.OpenAsync(f.Path,f.Cache)){Assert.True(index.RebuiltCorruptCache);Assert.Equal(2,(await index.EnsureLineAsync(2))!.LineNumber);}
        // BOM wins over a conflicting manual selection. A no-BOM source creates a separate encoding-specific index.
        var legacy=Fixture("plain\ntext","gb18030");string first;
        await using(var index=await TextLineIndex.OpenAsync(legacy.Path,legacy.Cache,"gb18030")){first=index.VersionKey;}
        await using(var index=await TextLineIndex.OpenAsync(legacy.Path,legacy.Cache,"utf-8")){Assert.NotEqual(first,index.VersionKey);}
    }
    [Theory][InlineData("utf-8")][InlineData("utf-16")][InlineData("utf-16BE")]
    public void BomIsNotRenderedWithExplicitEncoding(string name)
    {
        var f=Fixture("标题😀\r\n后文",name);using var reader=new BoundedTextReader(f.Path,name);
        var page=reader.ReadWindow(0);Assert.Equal("标题😀\r\n后文",page.Text);Assert.Equal(f.Encoding.GetPreamble().Length,page.Start);
        Assert.Equal(page.Next,page.ByteOffsetAt(page.Text.Length));
    }
}
