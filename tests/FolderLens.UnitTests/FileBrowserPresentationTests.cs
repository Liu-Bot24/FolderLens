using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class FileBrowserPresentationTests
{
    [Fact]
    public void DateSummaryUsesLocalCalendarAndKeepsExactTimeBoundaries()
    {
        var zone=TimeZoneInfo.CreateCustomTimeZone("test",TimeSpan.FromHours(8),"test","test");
        Assert.Equal("修改日期：2026-09-01 至 2026-09-07",DateRangeDisplay.Format(new("modified","utc","2026-08-31T16:00:00Z","2026-09-07T16:00:00Z"),zone));
        Assert.Contains("12:34:56",DateRangeDisplay.Format(new("captured","captureWall","2026-09-01T12:34:56","2026-09-02T12:34:56"),zone));
    }
    [Theory]
    [InlineData("report.PDF","other","PDF 文档")]
    [InlineData("report.xlsx","other","Excel 表格")]
    [InlineData("data.csv","text","CSV 表格")]
    [InlineData("readme.md","markdown","Markdown 文档")]
    [InlineData("record.mp3","audio","音频")]
    [InlineData("unknown.xyz","other","XYZ 文件")]
    [InlineData("no-extension","other","文件")]
    public void FileLabelsRemainUsefulWithoutDecoderMetadata(string name,string kind,string expected)=>Assert.Equal(expected,FileTypeDisplay.Label(name,kind));

    [Theory]
    [InlineData("image","pixelCount",true)]
    [InlineData("pdf","pixelCount",false)]
    [InlineData("all","durationMs",false)]
    [InlineData("audio","durationMs",true)]
    [InlineData("video","durationMs",true)]
    [InlineData("all","allocatedBytes",false)]
    [InlineData("documents","logicalBytes",true)]
    public void DefaultColumnsShowRelevantInformation(string category,string field,bool expected)=>Assert.Equal(expected,DetailColumnDefaults.IsVisible(category,field));
}
