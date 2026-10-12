using FolderLens.Contracts;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PixelPayloadTests
{
    [Fact] public void AcceptsPackedPixelsAndFullSize61MegapixelCameraPreview()
    {
        new PixelPayload(2,1,8,8).Validate();
        new PixelPayload(9504,6336,38016,240869376).Validate();
    }
    [Theory]
    [InlineData(2,1,7,8)]
    [InlineData(2,1,8,7)]
    [InlineData(0,1,0,0)]
    [InlineData(16385,1,65540,65540)]
    [InlineData(16384,16384,65536,1073741824)]
    public void RejectsMalformedOrUnboundedFrames(int width,int height,int stride,int bytes)
        =>Assert.Throws<InvalidDataException>(()=>new PixelPayload(width,height,stride,bytes).Validate());
    [Fact] public void RequiresAgreedAlphaColorAndPerRequestBudget()
    {
        var valid=new PixelPayload(2,1,8,8);
        Assert.Throws<InvalidDataException>(()=>valid.Validate(7));
        Assert.Throws<InvalidDataException>(()=>(valid with{AlphaMode="straight"}).Validate());
        Assert.Throws<InvalidDataException>(()=>(valid with{PixelFormat="BGRA8"}).Validate());
        Assert.Throws<InvalidDataException>(()=>(valid with{ColorSpace="scRGB"}).Validate());
    }
}
