using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class AppearancePreferencesTests
{
    [Theory]
    [InlineData("soft","soft")]
    [InlineData("native","native")]
    [InlineData("unrecognized","native")]
    [InlineData(null,"native")]
    public void UnrecognizedStylesFallBackToTheExistingAppearance(string? input,string expected)
        =>Assert.Equal(expected,new AppearancePreferences(input!).Normalize().Style);

    [Fact]
    public async Task ChoiceSurvivesReloadAndMissingSettingsKeepNativeAppearance()
    {
        var settings=new AtomicSettings(Path.Combine(Path.GetTempPath(),"FolderLens-appearance",Guid.NewGuid().ToString("N")));
        Assert.Null(await settings.Load<AppearancePreferences>("appearance.json"));
        Assert.Equal("native",new AppearancePreferences().Style);
        await settings.Save("appearance.json",new AppearancePreferences("soft"));
        Assert.Equal("soft",(await settings.Load<AppearancePreferences>("appearance.json"))!.Normalize().Style);
        await settings.Save("appearance.json",new AppearancePreferences("native"));
        Assert.Equal("native",(await settings.Load<AppearancePreferences>("appearance.json"))!.Normalize().Style);
    }
}
