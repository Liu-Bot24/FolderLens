using FolderLens.App;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DiagnosticPathsTests
{
    [Theory]
    [InlineData("--verify-image-switch",false)]
    [InlineData("--verify-image-switch",true)]
    [InlineData("--verify-gallery",false)]
    [InlineData("--verify-category-switch",true)]
    public async Task ReadOnlySourceRejectsDataAtOrBelowSourceBeforeStartup(string option,bool child)
    {
        string source=Path.Combine(Path.GetTempPath(),"FolderLens-path-only");
        string data=child?Path.Combine(source,"data"):source;
        await Assert.ThrowsAsync<ArgumentException>(()=>AppPaths.DataDirectory(["--verify-refresh",option,source,"--data-dir",data]));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public async Task SeparateSiblingOutputIsAllowed()
    {
        string source=Path.Combine(Path.GetTempPath(),"FolderLens-path-only");
        string data=source+"-output";
        Assert.Equal(data,await AppPaths.DataDirectory(["--verify-refresh","--verify-image-switch",source,"--data-dir",data]));
        Assert.False(Directory.Exists(source));Assert.False(Directory.Exists(data));
    }
}
