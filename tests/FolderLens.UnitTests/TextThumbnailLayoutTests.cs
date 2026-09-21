using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class TextThumbnailLayoutTests
{
    [Theory][InlineData(100)][InlineData(128)][InlineData(143)]
    public void SmallCardsHaveNoTextRead(int width)=>Assert.Equal(new TextThumbnailLayout(0,0,0),TextThumbnailLayout.ForWidth(width));
    [Fact] public void IncreasingSizeIncreasesVisibleContentWithinHardBudget()
    {
        var small=TextThumbnailLayout.ForWidth(160);var large=TextThumbnailLayout.ForWidth(300);
        Assert.True(large.Lines>small.Lines);Assert.True(large.Characters>small.Characters);Assert.True(large.ReadBytes>small.ReadBytes);
        Assert.InRange(small.ReadBytes,256,2048);Assert.InRange(large.ReadBytes,256,2048);
        Assert.Equal(TextThumbnailLayout.ForWidth(320),TextThumbnailLayout.ForWidth(10000));
    }
    [Fact] public void BlankLinesAreRemovedButIndentationAndContentRemain()
    {
        var layout=TextThumbnailLayout.ForWidth(300);
        Assert.Equal("# 标题\n  正文 A\n正文 B",layout.Display("\r\n  \r\n# 标题\r\n\r\n\t\r\n  正文 A\r正文 B\n\n"));
        Assert.Equal("连续正文\n第二行",layout.Display("连续正文\n第二行"));
        Assert.Equal("",layout.Display(" \n\t\n\r\n"));
        Assert.Equal("开头",layout.Display(new string('\n',1000)+"开头"));
    }
    [Fact] public void DisplayDoesNotSplitSurrogatePairOrRetainWholeDocument()
    {
        var layout=TextThumbnailLayout.ForWidth(160);string prefix=new('中',layout.Characters-1);
        Assert.True(layout.Display(new string('\t',1000)).Length<=layout.Characters);
        Assert.Equal(prefix,layout.Display(prefix+"😀"+new string('后',10000)));
    }
}
