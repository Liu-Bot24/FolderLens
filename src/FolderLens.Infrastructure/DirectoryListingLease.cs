using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

/// <summary>A process-held lease distinguishes live SQLite listings from crash leftovers.</summary>
internal sealed class DirectoryListingLease
{
    private static readonly ConcurrentDictionary<string,DirectoryListingLease> pendingDeletes=new();
    private readonly object gate=new();
    private FileStream? owner;
    private readonly List<SafeFileHandle> pins;
    internal string FilePath {get;}
    private DirectoryListingLease(string file,FileStream owner,List<SafeFileHandle> pins){FilePath=file;this.owner=owner;this.pins=pins;}
    private static List<SafeFileHandle> PinRoot(string directory)
    {
        directory=Path.GetFullPath(directory);var pins=new List<SafeFileHandle>();string current=Path.GetPathRoot(directory)!;
        try
        {
            pins.Add(ThumbnailCache.PinDirectory(current));
            foreach(string part in directory[current.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries))
            {current=Path.Combine(current,part);if(!Directory.Exists(current))Directory.CreateDirectory(current);pins.Add(ThumbnailCache.PinDirectory(current));}
            return pins;
        }
        catch{foreach(var pin in pins)pin.Dispose();throw;}
    }
    internal static DirectoryListingLease Create(string directory)
    {
        directory=Path.GetFullPath(directory);var pins=PinRoot(directory);
        try
        {
            Recover(directory);string file=Path.Combine(directory,Guid.NewGuid().ToString("N")+".sqlite");
            return new(file,new FileStream(ThumbnailCache.PinOwnedFile(file+".owner",0xC0000080,1,0),FileAccess.ReadWrite),pins);
        }
        catch{foreach(var pin in pins)pin.Dispose();throw;}
    }
    internal static void Recover(string directory)
    {
        directory=Path.GetFullPath(directory);var rootPins=PinRoot(directory);
        try
        {
            foreach(var pair in pendingDeletes)if(Path.GetDirectoryName(pair.Key)==directory)pair.Value.Delete();
            foreach(string marker in Directory.EnumerateFiles(directory,"*.sqlite.owner"))
            {
                string file=marker[..^6];if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out _))continue;
                FileStream claim;
                try{claim=new FileStream(ThumbnailCache.PinOwnedFile(marker,0xC0000080,3,0),FileAccess.ReadWrite);}
                catch(Win32Exception error) when(error.NativeErrorCode is 2 or 3 or 32 or 33){continue;}
                List<SafeFileHandle> leasePins;
                try{leasePins=PinRoot(directory);}catch{claim.Dispose();throw;}
                var lease=new DirectoryListingLease(file,claim,leasePins);lease.Delete();
            }
        }
        finally{foreach(var pin in rootPins)pin.Dispose();}
    }
    private static void DeleteIfPresent(string path)
    {try{ThumbnailCache.DeleteOwned(path);}catch(Win32Exception error) when(error.NativeErrorCode is 2 or 3){}}
    internal void Delete()
    {
        lock(gate)
        {
            try
            {
                // Hold the marker through data deletion so other processes cannot
                // mistake a live or partially retired listing for an abandoned one.
                if(owner is not null)
                {
                    DeleteIfPresent(FilePath);DeleteIfPresent(FilePath+"-journal");DeleteIfPresent(FilePath+"-wal");DeleteIfPresent(FilePath+"-shm");
                    owner.Dispose();owner=null;
                }
                DeleteIfPresent(FilePath+".owner");pendingDeletes.TryRemove(FilePath,out _);
                foreach(var pin in pins)pin.Dispose();pins.Clear();
            }
            catch{pendingDeletes[FilePath]=this;throw;}
        }
    }
}
