using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MarkdownTests
{
    [Fact] public async Task ActualWorkerRendersMarkdownWithoutScriptsOrRemoteImages()
    {
        var parent=new DirectoryInfo(AppContext.BaseDirectory);while(parent is not null&&!File.Exists(Path.Combine(parent.FullName,"FolderLens.slnx")))parent=parent.Parent;
        string root=parent!.FullName;string exe=Path.Combine(root,"src","FolderLens.Content.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Content.Worker.exe");
        string temp=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);string input=Path.Combine(temp,"unsafe.md");
        await File.WriteAllTextAsync(input,"# 标题\n\n<script>alert(1)</script>\n\n![secret](https://tracker.invalid/a.png)\n\n[bad](javascript:alert(1))\n\n![local](image.png)\n\n![video](clip.mp4)\n\n![remote video](https://tracker.invalid/clip.mp4)\n\n|a|b|\n|-|-|\n|1|2|");
        await using var worker=new WorkerClient(exe,Path.Combine(temp,"worker"));var reply=await worker.RenderMarkdown(input,new("root",1,1,1,1,1),CancellationToken.None);string html=await File.ReadAllTextAsync(reply.AssetPath!);
        Assert.Contains("<h1>",html);Assert.DoesNotContain("<script>",html);Assert.DoesNotContain("https://tracker.invalid",html);Assert.DoesNotContain("href=\"javascript:",html);Assert.Contains("default-src 'none'",html);Assert.Equal(2,reply.Message.Metadata!.Value.GetProperty("resources").GetArrayLength());
        Assert.Contains("data-src=\"https://folderlens.local/assets/",html);Assert.DoesNotContain("<img src=",html);
        Assert.Contains("<video controls preload=\"none\"",html);Assert.DoesNotContain("autoplay",html);
        Assert.Contains("aria-label=\"local\" />",html);Assert.Contains("aria-label=\"video\">",html);
        await worker.ReleaseAsset(reply);
        await File.WriteAllTextAsync(input,"![x \" onerror=\"alert(1)](image.png)");
        var hostile=await worker.RenderMarkdown(input,new("root",1,1,2,2,1),CancellationToken.None);string hostileHtml=await File.ReadAllTextAsync(hostile.AssetPath!);
        Assert.DoesNotContain(" onerror=\"",hostileHtml);Assert.Contains("&quot;",hostileHtml);
        await worker.ReleaseAsset(hostile);
    }
    [Theory][InlineData("https://remote.invalid/a.png")][InlineData("../secret.png")][InlineData("%2e%2e/secret.png")][InlineData("file:///C:/secret.png")]
    public void UnsafeLocalResourceIsRejectedBeforeFileAccess(string url)=>Assert.Throws<UnauthorizedAccessException>(()=>LocalResourceRules.ResolveImage(@"C:\root",@"C:\root\readme.md",url));
}
