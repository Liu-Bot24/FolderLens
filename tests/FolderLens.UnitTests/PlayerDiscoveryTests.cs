using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlayerDiscoveryTests
{
    [Theory]
    [InlineData(" \"D:\\Program Files\\PotPlayer\\PotPlayerMini64.exe\" ", "D:\\Program Files\\PotPlayer\\PotPlayerMini64.exe")]
    [InlineData("C:\\Apps\\PotPlayerMini.exe", "C:\\Apps\\PotPlayerMini.exe")]
    [InlineData("C:\\Apps\\POTPLAYERMINI64.EXE", "C:\\Apps\\POTPLAYERMINI64.EXE")]
    [InlineData("\"C:\\Apps\\PotPlayerMini64.exe\" /play", null)]
    [InlineData("C:\\Apps\\OtherPlayer.exe", null)]
    [InlineData("PotPlayerMini64.exe", null)]
    [InlineData("https://example.com/PotPlayerMini64.exe", null)]
    [InlineData("C:\\Apps\\\nPotPlayerMini64.exe", null)]
    [InlineData("", null)]
    public void RegistrationIsAPathToTheNamedPlayer(string input, string? expected)
        => Assert.Equal(expected, PlayerDiscovery.NormalizeRegisteredPath(input));
}
