using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record PlaylistCleanup(int Removed,int Failed,bool Incomplete);
public static class PlaylistFiles
{
    private const string Marker="#FolderLens-Playlist-v1:";
    public static string OwnershipHeader(DateTimeOffset created)=>Marker+created.UtcTicks.ToString(CultureInfo.InvariantCulture);

    // Only generated names AND a bounded ownership header qualify. Legacy unmarked
    // files and user-supplied playlists remain untouched.
    public static PlaylistCleanup Cleanup(string directory,DateTimeOffset now,CancellationToken cancellation=default)
    {
        directory=Path.GetFullPath(directory);if(!Directory.Exists(directory))return new(0,0,false);
        var pins=new List<SafeFileHandle>();int removed=0,failed=0,visited=0;
        try
        {
            string current=Path.GetPathRoot(directory)!;pins.Add(ThumbnailCache.PinDirectory(current));
            foreach(string part in directory[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
            {current=Path.Combine(current,part);pins.Add(ThumbnailCache.PinDirectory(current));}
            foreach(string path in Directory.EnumerateFiles(directory,"*.m3u8*",SearchOption.TopDirectoryOnly))
            {
                cancellation.ThrowIfCancellationRequested();if(++visited>10000)return new(removed,failed,true);
                string name=Path.GetFileName(path);int suffix=name.EndsWith(".m3u8.tmp",StringComparison.Ordinal)?".m3u8.tmp".Length:name.EndsWith(".m3u8",StringComparison.Ordinal)?".m3u8".Length:0;
                if(suffix==0||name.Length!=32+suffix||!Guid.TryParseExact(name[..32],"N",out _))continue;
                try
                {
                    using var stream=new FileStream(ThumbnailCache.PinOwnedFile(path,0x80010080),FileAccess.Read);
                    byte[] header=new byte[128];int read=stream.Read(header);string prefix=Encoding.UTF8.GetString(header,0,read);
                    string[] lines=prefix.Split('\n');if(lines.Length<3||lines[0].TrimEnd('\r')!="#EXTM3U")continue;
                    string owner=lines[1].TrimEnd('\r');if(!owner.StartsWith(Marker,StringComparison.Ordinal)||!long.TryParse(owner.AsSpan(Marker.Length),NumberStyles.None,CultureInfo.InvariantCulture,out long ticks))continue;
                    if(ticks<0||ticks>DateTime.MaxValue.Ticks||now.UtcTicks-ticks<TimeSpan.FromHours(24).Ticks)continue;
                    ThumbnailCache.DeleteOwned(stream.SafeFileHandle);removed++;
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception){failed++;}
            }
            return new(removed,failed,false);
        }
        finally{foreach(var pin in pins)pin.Dispose();}
    }
}
