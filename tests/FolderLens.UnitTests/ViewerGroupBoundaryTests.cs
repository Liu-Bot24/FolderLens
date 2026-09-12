using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ViewerGroupBoundaryTests
{
    [Fact]
    public void CrossGroupAndNewSessionAnnounceButSameGroupRedrawDoesNot()
    {
        var boundary = new ViewerGroupBoundary();
        Assert.True(boundary.Observe(true, "session1", "groupA"));
        Assert.False(boundary.Observe(true, "session1", "groupA"));
        Assert.True(boundary.Observe(true, "session1", "groupB"));
        Assert.True(boundary.Observe(true, "session1", "groupA"));
        Assert.True(boundary.Observe(true, "session2", "groupA"));
    }

    [Fact]
    public void BrowserAndUnresolvedSelectionClearOldBoundary()
    {
        var boundary = new ViewerGroupBoundary();
        Assert.False(boundary.Observe(false, "session", "group"));
        Assert.True(boundary.Observe(true, "session", "group"));
        Assert.False(boundary.Observe(true, "session", null));
        Assert.True(boundary.Observe(true, "session", "group"));
        Assert.False(boundary.Observe(true, null, "group"));
        Assert.False(boundary.Observe(false, "session", "group"));
        Assert.True(boundary.Observe(true, "session", "group"));
    }
}
