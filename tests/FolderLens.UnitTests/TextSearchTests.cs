using System.Text;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextSearchTests
{
    [Theory][InlineData("utf-8")][InlineData("utf-16")][InlineData("utf-16BE")][InlineData("gb18030")]
    public async Task SearchWrapsWithExactBytesAndHonestCompletion(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var encoding=Encoding.GetEncoding(name);
        string content="中文😀needle\r\n"+new string('a',65529)+"中文😀needle";
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"find.txt");
        File.WriteAllBytes(path,[..encoding.GetPreamble(),..encoding.GetBytes(content)]);
        long first=encoding.GetPreamble().Length,last=first+encoding.GetByteCount(content[..content.LastIndexOf("中文",StringComparison.Ordinal)]);
        var query=new TextSearchQuery("中文😀needle",StartOffset:last,QueryGeneration:42,FileVersion:9);
        var batches=new List<TextSearchBatch>();await foreach(var batch in TextSearch.SearchAsync(path,query,name))batches.Add(batch);
        var matches=batches.SelectMany(b=>b.Matches).ToArray();Assert.Equal(new[]{last,first},matches.Select(m=>m.ByteOffset));
        Assert.All(matches,m=>Assert.Equal(encoding.GetByteCount(query.Literal),m.ByteLength));
        Assert.All(batches,b=>{Assert.Equal(42,b.QueryGeneration);Assert.Equal(9,b.FileVersion);Assert.InRange(b.Matches.Count,0,100);});
        Assert.True(batches[^1].IsFinal);Assert.True(batches[^1].Complete);Assert.False(batches[^1].LimitReached);Assert.True(batches[^1].Wrapped);
        var absent=new List<TextSearchBatch>();await foreach(var batch in TextSearch.SearchAsync(path,new("not present"),name))absent.Add(batch);
        Assert.Empty(absent.SelectMany(b=>b.Matches));Assert.True(absent[^1].Complete);
    }
    [Fact] public async Task LimitIsNotACompleteCountAndCancellationIsObserved()
    {
        string path=Path.Combine(Path.GetTempPath(),"FolderLens-search-"+Guid.NewGuid().ToString("N")+".txt");File.WriteAllText(path,"aaaaa",new UTF8Encoding(false));
        var batches=new List<TextSearchBatch>();await foreach(var batch in TextSearch.SearchAsync(path,new("a",MaxHits:2,BatchSize:1)))batches.Add(batch);
        Assert.Equal(2,batches.SelectMany(b=>b.Matches).Count());Assert.True(batches[^1].LimitReached);Assert.False(batches[^1].Complete);Assert.True(batches[^1].IsFinal);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>{await foreach(var batch in TextSearch.SearchAsync(path,new("a"),cancellation:cancel.Token)){} });
    }
}
