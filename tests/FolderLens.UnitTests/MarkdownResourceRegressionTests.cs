using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MarkdownResourceRegressionTests
{
    [Fact]
    public void OfflineEmbeddedImageIsDeniedBeforeOpeningContent()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string image=Path.Combine(root,"online.png");File.WriteAllText(image,"fixture");File.SetAttributes(image,FileAttributes.Offline);
        Assert.True(FileAllocation.IsDeferred((long)File.GetAttributes(image)));
        using var locked=File.Open(image,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        Assert.Throws<UnauthorizedAccessException>(()=>LocalResourceRules.ResolveImage(root,Path.Combine(root,"note.md"),"online.png"));
    }
    [Fact]
    public void LocalEmbeddedImageWithinRealRootIsAllowed()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"image.png"),"fixture");
        Assert.EndsWith("image.png",LocalResourceRules.ResolveImage(root,Path.Combine(root,"note.md"),"image.png"));
        Assert.Throws<UnauthorizedAccessException>(()=>LocalResourceRules.ResolveImage(root,Path.Combine(root,"note.md"),"../image.png"));
    }
}
