using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class ImageViewportTests
{
    [Fact]public void RotatedViewportUsesImageAxesAndDoesNotFetchTheOppositeEdge()
    {
        var right=ImageViewport.VisibleTiles(8192,2048,1024,2048,1,-3072,0,0,padding:0);
        Assert.Equal(new TileRange(6,0,7,1),right);
        var rotated=ImageViewport.VisibleTiles(8192,2048,2048,1024,1,-3072,0,1,padding:0);
        Assert.Equal(right,rotated);Assert.NotEqual(0,rotated.FirstX);
        Assert.Equal(new TileRange(0,0,1,1),ImageViewport.VisibleTiles(8192,2048,2048,1024,1,3072,0,3,padding:0));
    }
    [Fact]public void FitRangeIsBoundedAndInvalidScaleIsRejected()
    {
        Assert.Equal(16,ImageViewport.VisibleTiles(4096,4096,1024,1024,.25,0,0,0).Count);
        Assert.Throws<ArgumentOutOfRangeException>(()=>ImageViewport.VisibleTiles(4096,4096,1024,1024,0,0,0,0));
    }
}
