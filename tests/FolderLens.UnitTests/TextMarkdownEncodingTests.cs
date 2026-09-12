using System.Text;
using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextMarkdownEncodingTests
{
    private static string Worker()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"FolderLens.slnx")))directory=directory.Parent;
        return Path.Combine(directory!.FullName,"src","FolderLens.Content.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Content.Worker.exe");
    }
    [Theory][InlineData("utf-16")][InlineData("utf-16BE")]
    public async Task ActualContentWorkerReadsUnicodeBom(string name)
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"unicode.md");File.WriteAllText(path,"# 标题😀\r\n\r\n中文正文",Encoding.GetEncoding(name));
        await using var worker=new WorkerClient(Worker(),Path.Combine(directory,"worker"));
        var reply=await worker.RenderMarkdown(path,new("root",1,1,1,1,1),CancellationToken.None);
        string html=File.ReadAllText(reply.AssetPath!);Assert.Contains("<h1>",html);Assert.Contains("中文正文",html);Assert.Equal(Encoding.GetEncoding(name).WebName,reply.Message.Metadata!.Value.GetProperty("encoding").GetString());
        await worker.ReleaseAsset(reply);
    }
    [Fact] public async Task CarriageReturnOnlyMarkdownCannotBypassLineLimit()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"many.md");
        File.WriteAllText(path,string.Concat(Enumerable.Repeat("a\r",50_000)),new UTF8Encoding(false));
        await using var worker=new WorkerClient(Worker(),Path.Combine(directory,"worker"));
        var exception=await Assert.ThrowsAsync<InvalidDataException>(()=>worker.RenderMarkdown(path,new("root",1,1,1,1,1),CancellationToken.None));Assert.Contains("MarkdownLineLimit",exception.Message);
    }
    [Fact]public async Task WorkerObservedMarkdownStillHonorsIndexedVersionAndInputLimit()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-md-version",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"source.md");await File.WriteAllTextAsync(path,"# original");
        var stat=new FileInfo(path);var indexed=new SourceFileStamp(stat.Length,stat.LastWriteTimeUtc.Ticks);
        var context=new RequestContext("root",1,1,1,1,1);await using var worker=new WorkerClient(Worker(),Path.Combine(directory,"worker"));
        var original=await worker.RenderMarkdown(path,context,CancellationToken.None,sourceStamp:indexed);await worker.ReleaseAsset(original);
        await File.WriteAllTextAsync(path,"# replacement with another length");
        Assert.Equal("FileChanged",(await Assert.ThrowsAsync<IOException>(()=>worker.RenderMarkdown(path,context,CancellationToken.None,sourceStamp:indexed))).Message);
        var refreshed=await worker.RenderMarkdown(path,context,CancellationToken.None);Assert.Contains("replacement",await File.ReadAllTextAsync(refreshed.AssetPath!));await worker.ReleaseAsset(refreshed);
        using(var large=new FileStream(path,FileMode.Create,FileAccess.Write)){large.SetLength(9L*1024*1024);}
        Assert.Contains("MarkdownInputLimit",(await Assert.ThrowsAsync<InvalidDataException>(()=>worker.RenderMarkdown(path,context,CancellationToken.None))).Message);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>worker.RenderMarkdown(Path.Combine(directory,"absent.md"),context,CancellationToken.None));
    }
}
