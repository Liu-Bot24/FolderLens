using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class TextThumbnailLayoutTests
{
    [Theory][InlineData(100)][InlineData(128)][InlineData(143)]
    public void SmallCardsHaveNoTextRead(int width)=>Assert.Equal(new TextThumbnailLayout(0,0,0),TextThumbnailLayout.ForWidth(width));
    [Fact] public void IncreasingSizeIncreasesVisibleContentWithinHardBudget()
    {
        var small=TextThumbnailLayout.ForWidth(160);var large=TextThumbnailLayout.ForWidth(240);
        Assert.True(large.Lines>small.Lines);Assert.True(large.Characters>small.Characters);Assert.True(large.ReadBytes>small.ReadBytes);
        Assert.InRange(small.ReadBytes,256,1024);Assert.InRange(large.ReadBytes,256,1024);
        Assert.Equal(TextThumbnailLayout.ForWidth(260),TextThumbnailLayout.ForWidth(10000));
    }
    [Fact] public void DisplayDoesNotSplitSurrogatePairOrRetainWholeDocument()
    {
        var layout=TextThumbnailLayout.ForWidth(160);string prefix=new('中',layout.Characters-1);
        Assert.True(layout.Display(new string('\t',1000)).Length<=layout.Characters);
        Assert.Equal(prefix,layout.Display(prefix+"😀"+new string('后',10000)));
    }
}
