using FolderLens.Core;
using System.Text.Json;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PreviewBookmarkTests
{
    [Fact]public void KeepsLargeOffsetsAndEncodingAcrossPersistence()
    {
        var bookmark=new PreviewBookmark(6L*1024*1024*1024,123,"text",TextOffset:5L*1024*1024*1024,Encoding:"gb18030",TextScroll:180);
        var restored=JsonSerializer.Deserialize<PreviewBookmark>(JsonSerializer.Serialize(bookmark))!.ForFile(bookmark.Bytes,123,"text");
        Assert.Equal(bookmark,restored);
    }
    [Fact]public void RejectsChangedFileAndClampsInvalidPositions()
    {
        var bookmark=new PreviewBookmark(100,123,"image",3);
        Assert.Null(bookmark.ForFile(101,123,"image"));Assert.Null(bookmark.ForFile(100,124,"image"));Assert.Null(bookmark.ForFile(100,123,"text"));
        Assert.Equal(3,bookmark.ForFile(100,123,"image")!.ImagePage);
        var invalid=new PreviewBookmark(100,123,"text",-8,long.MaxValue,"unsupported",double.NaN);
        var safe=invalid.ForFile(100,123,"text")!;
        Assert.Equal(0,safe.ImagePage);Assert.Equal(99,safe.TextOffset);Assert.Null(safe.Encoding);Assert.Equal(0,safe.TextScroll);
        Assert.Equal(0,new PreviewBookmark(0,123,"text",TextOffset:10).ForFile(0,123,"text")!.TextOffset);
    }
}
