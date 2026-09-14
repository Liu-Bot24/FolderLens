using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class DirectoryBrowseScopeTests
{
    [Theory]
    [InlineData(@"D:\photos",@"D:\photos\trip", "trip")]
    [InlineData(@"D:\photos",@"D:\photos", "")]
    [InlineData(@"D:\",@"D:\photos", "photos")]
    [InlineData(@"D:\photos",@"D:\photos-old",null)]
    [InlineData(@"D:\photos",@"D:\Photos\trip",null)]
    [InlineData(@"D:\photos",@"E:\photos\trip",null)]
    public void ReusesOnlyExactPathScope(string root,string path,string? expected)=>Assert.Equal(expected,DirectoryBrowseScope.Relative(root,path));
}
