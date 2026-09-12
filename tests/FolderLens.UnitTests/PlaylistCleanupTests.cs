using System.Runtime.InteropServices;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlaylistCleanupTests
{
    [Fact]
    public void RetainsOneDayAndOnlyRemovesGeneratedUnlinkedFiles()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-playlist-cleanup",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var now=DateTimeOffset.UtcNow;
        string Write(TimeSpan age,string? name=null,string suffix=".m3u8")
        {string path=Path.Combine(directory,name??Guid.NewGuid().ToString("N")+suffix);File.WriteAllText(path,"#EXTM3U\n"+PlaylistFiles.OwnershipHeader(now-age)+"\nC:\\sample.mp4\n");return path;}
        string expired=Write(TimeSpan.FromHours(25)),young=Write(TimeSpan.FromHours(23)),boundary=Write(TimeSpan.FromHours(24)),future=Write(TimeSpan.FromHours(-1)),unfinished=Write(TimeSpan.FromHours(25),suffix:".m3u8.tmp");
        string custom=Write(TimeSpan.FromHours(25),"user-list.m3u8"),legacy=Path.Combine(directory,Guid.NewGuid().ToString("N")+".m3u8");File.WriteAllText(legacy,"#EXTM3U\nC:\\legacy.mp4\n");
        string hard=Write(TimeSpan.FromHours(25)),alias=Path.Combine(directory,"alias.txt");Assert.True(CreateHardLinkW(alias,hard,IntPtr.Zero));
        var result=PlaylistFiles.Cleanup(directory,now);Assert.Equal(3,result.Removed);Assert.Equal(1,result.Failed);Assert.False(result.Incomplete);
        foreach(string file in new[]{expired,boundary,unfinished})Assert.False(File.Exists(file));
        foreach(string file in new[]{young,future,custom,legacy,hard,alias})Assert.True(File.Exists(file));
        using var stop=new CancellationTokenSource();stop.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>PlaylistFiles.Cleanup(directory,now,stop.Token));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
}
