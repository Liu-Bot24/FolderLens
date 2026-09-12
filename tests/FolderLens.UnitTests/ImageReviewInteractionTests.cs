using System.Text.Json;
using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ImageReviewInteractionTests
{
    [Fact]
    public void SortFieldsFollowMediaCategoryAndCommonFieldsRemainAvailable()
    {
        foreach(string category in new[]{"image","video","audio","text","media","all"})
        {
            Assert.True(BrowserSortOptions.IsApplicable(category,"logicalBytes"));
            Assert.Equal(category is "video" or "audio" or "media" or "all",BrowserSortOptions.IsApplicable(category,"durationMs"));
            Assert.Equal(category is "image" or "video" or "media" or "all",BrowserSortOptions.IsApplicable(category,"pixelCount"));
        }
        Assert.False(BrowserSortOptions.IsApplicable("image","invalid"));
    }
    [Fact]
    public void HoldPreferencesDefaultToWholeLargeViewButKeepSidebarLensAndRoundTrip()
    {
        var defaults=JsonSerializer.Deserialize<PressZoomOptions>("{}")!.Normalize();
        Assert.True(defaults.UsesWholeImage(true));Assert.False(defaults.UsesWholeImage(false));Assert.Equal(250,defaults.Percent);
        var changed=JsonSerializer.Deserialize<PressZoomOptions>(JsonSerializer.Serialize(new PressZoomOptions("lens",200)))!.Normalize();
        Assert.False(changed.UsesWholeImage(true));Assert.False(changed.UsesWholeImage(false));Assert.Equal(200,changed.Percent);
        Assert.Equal(new PressZoomOptions("whole",250),new PressZoomOptions("invalid",double.NaN).Normalize());
        Assert.Equal(100,new PressZoomOptions("whole",100).Normalize().Percent);
        Assert.Equal(800,new PressZoomOptions("whole",900).Normalize().Percent);
    }
    [Fact]
    public void WholeImageFocusClampsEdgesWithQuarterTurnAndSmallImageCentering()
    {
        Assert.Equal((250d,125d),PressZoomOptions.ClampCenter(0,0,3000,2000,1000,500,2,0));
        Assert.Equal((125d,250d),PressZoomOptions.ClampCenter(0,0,3000,2000,1000,500,2,1));
        Assert.Equal((1500d,1000d),PressZoomOptions.ClampCenter(1500,1000,3000,2000,1000,500,2,0));
        Assert.Equal((50d,25d),PressZoomOptions.ClampCenter(0,0,100,50,1000,500,1,0));
        Assert.Equal((2750d,1875d),PressZoomOptions.ClampCenter(9999,9999,3000,2000,1000,500,2,0));
    }
}
