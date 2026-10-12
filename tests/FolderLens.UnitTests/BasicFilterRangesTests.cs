using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class BasicFilterRangesTests
{
    [Fact]
    public void EmptyMinimaClearRestoredValuesAndKeepAdvancedMaxima()
    {
        var saved=new Dictionary<string,IntRange>{{"width",new(1920,4096)},{"height",new(1080,null)},{"durationMs",new(10,20)}};
        var result=BasicFilterRanges.Merge(saved,double.NaN,double.NaN,double.NaN,double.NaN);
        Assert.Equal(new IntRange(null,4096),result["width"]);Assert.False(result.ContainsKey("height"));
        Assert.Equal(saved["durationMs"],result["durationMs"]);Assert.Equal(1920,saved["width"].Min);
        Assert.Equal(new IntRange(1048576,2097152),BasicFilterRanges.Merge(null,1,2,100,100)["logicalBytes"]);
    }
    [Theory]
    [InlineData(2,1)]
    [InlineData(double.MaxValue,double.NaN)]
    [InlineData(double.PositiveInfinity,double.NaN)]
    [InlineData(-1,2)]
    public void InvalidDraftIsRejected(double min,double max)=>Assert.Throws<ArgumentException>(()=>BasicFilterRanges.Merge(null,min,max,double.NaN,double.NaN));

    [Fact]
    public void ExactDimensionTextKeepsInt64AndExplicitClear()
    {
        var saved=new Dictionary<string,IntRange>{{"width",new(9007199254740993,long.MaxValue)},{"height",new(long.MaxValue,null)},{"durationMs",new(10,20)}};
        var exact=BasicFilterRanges.MergeExactDimensions(saved,double.NaN,double.NaN,"9007199254740993",long.MaxValue.ToString());
        Assert.Equal(saved["width"],exact["width"]);Assert.Equal(saved["height"],exact["height"]);
        var edited=BasicFilterRanges.MergeExactDimensions(saved,double.NaN,double.NaN,"9007199254740992","");
        Assert.Equal(new IntRange(9007199254740992,long.MaxValue),edited["width"]);Assert.False(edited.ContainsKey("height"));
        Assert.Equal(saved["durationMs"],edited["durationMs"]);
        var cleared=BasicFilterRanges.MergeExactDimensions(saved,double.NaN,double.NaN,"","");
        Assert.Equal(new IntRange(null,long.MaxValue),cleared["width"]);
    }
    [Theory]
    [InlineData("-1")][InlineData("1.5")][InlineData("9223372036854775808")][InlineData("abc")]
    public void ExactDimensionDraftRejectsInvalidText(string text)=>Assert.Throws<ArgumentException>(()=>BasicFilterRanges.MergeExactDimensions(null,double.NaN,double.NaN,text,""));
}
