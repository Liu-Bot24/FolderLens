using System.Security.Cryptography;
using System.Text;
using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextRemoteTests
{
    [Theory][InlineData("window")][InlineData("find")][InlineData("index")][InlineData("excerpt")]
    public async Task FirstOperationRejectsReplacementAgainstApprovedSignature(string operation)
    {
        var f=Fixture("first\r\nsecond","utf-8");var observed=FileReadObservation.Read(f.Path);
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));
        var remote=new RemoteTextClient(worker,f.Path,new("root",1,1,1,1,1),sourceStamp:new(observed.Length,observed.ModifiedUtcTicks,observed.Signature));
        var bytes=File.ReadAllBytes(f.Path);var written=File.GetLastWriteTimeUtc(f.Path);
        File.Move(f.Path,f.Path+".old");File.WriteAllBytes(f.Path,bytes);File.SetLastWriteTimeUtc(f.Path,written);
        await Assert.ThrowsAsync<IOException>(async()=>
        {
            if(operation=="excerpt")await remote.ReadExcerpt(512);
            else if(operation=="window")await remote.ReadWindow(0);
            else if(operation=="find")await remote.FindNext("first");
            else await remote.IndexStep();
        });
    }
    [Theory][InlineData("utf-8")][InlineData("utf-16")][InlineData("utf-16BE")][InlineData("gb18030")]
    public async Task ExcerptUsesOnlyBoundedPrefixAndKeepsCompleteCharacters(string name)
    {
        string content=string.Concat(Enumerable.Repeat("开头文字😀第一行，第二行。\r\n",400));var f=Fixture(content,name);
        // The unrelated tail is deliberately invalid text. An unbounded detection
        // sample or document read would reject this file instead of returning its prefix.
        using(var file=new FileStream(f.Path,FileMode.Open,FileAccess.Write)){file.SetLength(8L<<20);}
        var observed=FileReadObservation.Read(f.Path);DateTime written=File.GetLastWriteTimeUtc(f.Path);
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));
        var remote=new RemoteTextClient(worker,f.Path,new("root",1,1,1,1,1),sourceStamp:new(observed.Length,observed.ModifiedUtcTicks,observed.Signature));
        var page=await remote.ReadExcerpt(512);
        Assert.StartsWith("开头文字😀第一行",page.Text);Assert.StartsWith(page.Text,content,StringComparison.Ordinal);
        Assert.InRange(page.Next-page.Start,508,512);Assert.False(page.AtEnd);Assert.Equal(8L<<20,page.Length);
        Assert.DoesNotContain('\uFFFD',page.Text);Assert.False(char.IsHighSurrogate(page.Text[^1]));
        Assert.Null(remote.IndexProgress);Assert.Equal(written,File.GetLastWriteTimeUtc(f.Path));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>remote.ReadExcerpt(2048));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>remote.ReadExcerpt(512,cancel.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root,"worker"),"*.png",SearchOption.AllDirectories));
    }
    [Fact] public async Task ExcerptPreservesEmptyAndBinaryStates()
    {
        var f=Fixture("","utf-8");
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));
        var empty=await new RemoteTextClient(worker,f.Path,new("root",1,1,1,1,1)).ReadExcerpt(256);
        Assert.Empty(empty.Text);Assert.True(empty.AtEnd);
        File.WriteAllBytes(f.Path,new byte[512]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>new RemoteTextClient(worker,f.Path,new("root",1,1,1,2,1)).ReadExcerpt(256));
    }
    private static string Worker()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"FolderLens.slnx")))directory=directory.Parent;
        return Path.Combine(directory!.FullName,"src","FolderLens.Content.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Content.Worker.exe");
    }
    private static (string Path,string Root,Encoding Encoding) Fixture(string content,string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var encoding=Encoding.GetEncoding(name);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"source.txt");
        File.WriteAllBytes(path,[..encoding.GetPreamble(),..encoding.GetBytes(content)]);return(path,directory,encoding);
    }
    [Theory][InlineData("utf-8")][InlineData("utf-16BE")][InlineData("gb18030")]
    public async Task ActualWorkerSupportsWindowWrappedSearchAndDiskLineSteps(string name)
    {
        string content=string.Join("\r\n",Enumerable.Range(1,20_000).Select(n=>$"第{n}行😀"))+"\r\n";var f=Fixture(content,name);
        byte[] hash=SHA256.HashData(File.ReadAllBytes(f.Path));DateTime written=File.GetLastWriteTimeUtc(f.Path);
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));var context=new RequestContext("root",1,4,5,9,1);
        var remote=new RemoteTextClient(worker,f.Path,context,name);var page=await remote.ReadWindow(0);
        Assert.StartsWith("第1行😀\r\n",page.Text);Assert.InRange(page.Next-page.Start,1,64*1024);Assert.Equal(page.Next,page.ByteOffsetAt(page.Text.Length));
        Assert.False((await remote.IndexStep(1)).Complete);
        long expected=f.Encoding.GetPreamble().Length+f.Encoding.GetByteCount(content[..content.IndexOf("第18000行",StringComparison.Ordinal)]);
        Assert.Equal(new TextPosition(expected,18_000,1),await remote.FindLine(18_000));
        var found=await remote.FindNext("第1行😀",startOffset:page.Next);Assert.NotNull(found.Match);Assert.True(found.Wrapped);Assert.Equal(f.Encoding.GetPreamble().Length,found.Match!.ByteOffset);
        var absent=await remote.FindNext("this literal is absent");Assert.Null(absent.Match);Assert.True(absent.SearchExhausted);
        Assert.Equal(content,await remote.ReadDocumentForCopy(content.Length));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>remote.ReadDocumentForCopy(content.Length-1));
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(f.Path)));Assert.Equal(written,File.GetLastWriteTimeUtc(f.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root,"worker"),"*.png",SearchOption.AllDirectories));
    }
    [Fact] public async Task StrongSnapshotRejectsSamePathReplacementWithSameLengthAndTimestamp()
    {
        var f=Fixture("first\r\nsecond","utf-8");var bytes=File.ReadAllBytes(f.Path);DateTime time=File.GetLastWriteTimeUtc(f.Path);
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));var remote=new RemoteTextClient(worker,f.Path,new("root",1,1,1,1,1));await remote.ReadWindow(0);
        File.Move(f.Path,f.Path+".old");File.WriteAllBytes(f.Path,bytes);File.SetLastWriteTimeUtc(f.Path,time);
        await Assert.ThrowsAsync<IOException>(()=>remote.ReadWindow(0));
    }
    [Fact] public async Task DataCancellationRecyclesActualWorkerAndDoesNotCreatePictureAssets()
    {
        var f=Fixture(string.Concat(Enumerable.Repeat("abcdef\n",200_000)),"utf-8");
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));var context=new RequestContext("root",1,1,1,1,1);
        var first=await worker.RequestData(f.Path,"textWindow",context,new TextWorkerParameters(),CancellationToken.None);Assert.Null(first.AssetPath);Assert.Null(first.Message.AssetToken);
        using var cancel=new CancellationTokenSource();var pending=worker.RequestData(f.Path,"textIndexStep",context,new TextWorkerParameters(StepPages:16),cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending.WaitAsync(TimeSpan.FromSeconds(3)));
        var next=await worker.RequestData(f.Path,"textWindow",context,new TextWorkerParameters(),CancellationToken.None);Assert.NotEqual(first.Message.WorkerInstanceId,next.Message.WorkerInstanceId);Assert.Null(next.AssetPath);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root,"worker"),"*.png",SearchOption.AllDirectories));
    }
    [Fact] public async Task BatchedSearchReportsCompleteAndLimitedWithoutTruncatingSilently()
    {
        var f=Fixture(string.Concat(Enumerable.Repeat("命中😀\r\n",65)),"utf-8");
        await using var worker=new WorkerClient(Worker(),Path.Combine(f.Root,"worker"));var remote=new RemoteTextClient(worker,f.Path,new("root",1,3,4,5,1));
        var batches=new List<TextSearchBatch>();await foreach(var batch in remote.FindAll("命中😀"))batches.Add(batch);
        Assert.All(batches,b=>Assert.InRange(b.Matches.Count,0,32));
        var matches=batches.SelectMany(b=>b.Matches).ToArray();Assert.Equal(65,matches.Length);Assert.Equal(65,matches.Select(m=>m.ByteOffset).Distinct().Count());
        Assert.True(batches[^1].Complete);Assert.False(batches[^1].LimitReached);
        batches.Clear();await foreach(var batch in remote.FindAll("命中😀",maxHits:40))batches.Add(batch);
        Assert.Equal(40,batches.Sum(b=>b.Matches.Count));Assert.True(batches[^1].LimitReached);Assert.False(batches[^1].Complete);
        batches.Clear();await foreach(var batch in remote.FindAll("不存在"))batches.Add(batch);
        Assert.Empty(batches.SelectMany(b=>b.Matches));Assert.True(batches[^1].Complete);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>remote.ReadDocumentForCopy(cancellation:cancel.Token));
    }
}
