using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class NavigationDriveNamesTests
{
    [Theory]
    [InlineData(@"U:\",@"Photos (\\EQ14) (U:)","U: Photos")]
    [InlineData(@"T:\",@"Acer SSD N5000 (\\mac-mini.local) (T:)","T: Acer SSD N5000")]
    [InlineData(@"C:\","本地磁盘 (C:)","C: 本地磁盘")]
    [InlineData(@"D:\","新加卷 (D:)","D: 新加卷")]
    [InlineData(@"D:\","照片 (旧) (D:)","D: 照片 (旧)")]
    [InlineData(@"d:\","Archive (d:)","D: Archive")]
    [InlineData(@"U:\","U:","U:")]
    [InlineData(@"U:\",null,"U:")]
    public void NamesRetainLettersAndOmitOnlyShellAddressSuffixes(string root,string? display,string expected)
        =>Assert.Equal(expected,NavigationDriveNames.Format(root,display));
    [Theory]
    [InlineData(@"\\server\share")][InlineData(@"C:\folder")][InlineData("C:")]
    public void NameHelperAcceptsOnlyDriveRoots(string input)=>Assert.Throws<ArgumentException>(()=>NavigationDriveNames.Format(input,"label"));
    [Fact]
    public async Task RealLocalDriveNameComesFromTheIsolatedHelper()
    {
        string root=Path.GetPathRoot(AppContext.BaseDirectory)!;
        string executable=ScanWorkerClient.FindExecutable()??throw new FileNotFoundException("Scan worker missing.");
        string label=await NavigationDriveNames.Read(root,executable,default);
        Assert.StartsWith(root[..2]+" ",label);Assert.DoesNotContain(root[..2]+")",label);
    }
}
