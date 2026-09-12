using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class NavigationHistoryTests
{
    [Fact]public void BackAndForwardKeepLatestScrollStateAndNewVisitClearsForward()
    {
        var history=new NavigationHistory<(string Path,int Scroll)>();
        history.VisitFrom(("root",240));history.VisitFrom(("child",800));
        Assert.Equal(("child",800),history.GoBack(("grandchild",500)));
        Assert.Equal(("grandchild",500),history.GoForward(("child",1000)));
        Assert.Equal(("child",1000),history.GoBack(("grandchild",600)));
        history.VisitFrom(("child",1100));Assert.False(history.CanGoForward);
        Assert.Throws<InvalidOperationException>(()=>history.GoForward(("other",0)));
        Assert.Equal(("child",1100),history.GoBack(("other",0)));
    }
    [Fact]public void HistoryIsBoundedAndEmptyNavigationDoesNotCreateEntries()
    {
        var history=new NavigationHistory<int>();
        Assert.Throws<InvalidOperationException>(()=>history.GoBack(0));Assert.False(history.CanGoForward);
        for(int i=0;i<150;i++)history.VisitFrom(i);
        for(int i=149;i>=50;i--)Assert.Equal(i,history.GoBack(i+1));
        Assert.False(history.CanGoBack);Assert.True(history.CanGoForward);
    }
}
