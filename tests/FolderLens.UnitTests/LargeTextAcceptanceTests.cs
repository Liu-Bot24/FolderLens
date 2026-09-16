using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FolderLens.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FolderLens.UnitTests;

public sealed class LargeTextAcceptanceTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "LargeFixture")]
    public async Task ActualWorkerReadsAndSearches256MiBSingleLineAndRejectsChangedSource()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);
        while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        string workerPath=Path.Combine(project!.FullName,"src","FolderLens.Content.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Content.Worker.exe");
        string directory=Path.Combine(project.FullName,"artifacts","text-single-line",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"source.txt");
        const long lineBytes=256L*1024*1024, markerOffset=128L*1024*1024-2;
        const string marker="中文😀needle";
        byte[] block=new byte[1024*1024];Array.Fill(block,(byte)'a');
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            for(long written=0;written<lineBytes;written+=block.Length)file.Write(block);
            file.Write(Encoding.UTF8.GetBytes("\r\ntail"));
            file.Position=markerOffset;file.Write(Encoding.UTF8.GetBytes(marker));file.Flush(true);
        }
        static byte[] Hash(string source){using var file=File.OpenRead(source);return SHA256.HashData(file);}
        byte[] before=Hash(path);DateTime modified=File.GetLastWriteTimeUtc(path);
        await using var worker=new WorkerClient(workerPath,Path.Combine(directory,"worker"));
        var remote=new RemoteTextClient(worker,path,new("single-line",1,1,1,1,1),"utf-8");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var clock=Stopwatch.StartNew();
        var first=await remote.ReadWindow(0,cancellation:timeout.Token);
        Assert.Equal(lineBytes+6,first.Length);Assert.Equal(new string('a',64*1024),first.Text);Assert.False(first.AtEnd);
        foreach(long offset in new[]{7L,64*1024-3,64L*1024*1024+17,markerOffset,lineBytes-32})
        {
            var page=await remote.ReadWindow(offset,cancellation:timeout.Token);
            Assert.Equal(offset,page.Start);Assert.InRange(page.Next-page.Start,1,64*1024);
            Assert.Equal(page.Next,page.ByteOffsetAt(page.Text.Length));
        }
        Assert.StartsWith(marker,(await remote.ReadWindow(markerOffset,cancellation:timeout.Token)).Text);
        var found=await remote.FindNext(marker,startOffset:markerOffset-64*1024,wrap:false,cancellation:timeout.Token);
        Assert.Equal(markerOffset,found.Match!.ByteOffset);Assert.False(found.Wrapped);
        var absent=await remote.FindNext("not-present-in-this-fixture",wrap:false,cancellation:timeout.Token);
        Assert.Null(absent.Match);Assert.True(absent.SearchExhausted);
        Assert.Equal(new TextPosition(lineBytes+2,2,1),await remote.FindLine(2,cancellation:timeout.Token));
        Assert.Equal("tail",(await remote.ReadWindow(lineBytes+2,cancellation:timeout.Token)).Text);
        Assert.Null(await remote.FindLine(3,cancellation:timeout.Token));
        Assert.Equal(before,Hash(path));Assert.Equal(modified,File.GetLastWriteTimeUtc(path));
        output.WriteLine($"Actual non-sparse single line: {lineBytes} bytes; read/search/index {clock.Elapsed.TotalMilliseconds:F1} ms; SHA256 {Convert.ToHexString(before)}; fixture {Path.GetFileName(directory)}. Backend IPC verification, not physical UI layout or a process-memory gate.");
        await File.AppendAllTextAsync(path,"-appended",timeout.Token);
        await Assert.ThrowsAsync<IOException>(()=>remote.ReadWindow(0,cancellation:timeout.Token));
        var appended=new RemoteTextClient(worker,path,new("single-line",1,2,2,2,1),"utf-8");
        Assert.Equal("tail-appended",(await appended.ReadWindow(lineBytes+2,cancellation:timeout.Token)).Text);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))file.SetLength(1024);
        await Assert.ThrowsAsync<IOException>(()=>appended.ReadWindow(0,cancellation:timeout.Token));
    }
}
