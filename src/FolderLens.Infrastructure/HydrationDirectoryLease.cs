using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

// Only held during an explicitly approved hydration in the owned worker.
// Deny write/delete sharing to prevent renames and reparse retargeting while
// a path-based open is used; reject any unsupported reparse component.
internal sealed class HydrationDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> pins=[];
    internal HydrationDirectoryLease(string directory,SourceParentAuthorization expected)
    {
        if(string.IsNullOrEmpty(expected.Identity)||string.IsNullOrEmpty(expected.ResolvedLocation))throw new IOException("MissingParentAuthorization");
        directory=Path.GetFullPath(directory);
        try
        {
            string current=Path.GetPathRoot(directory)!;Pin(current);
            foreach(string part in directory[current.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries)){current=Path.Combine(current,part);Pin(current);}
            var actual=FileAllocation.InspectMetadata(pins[^1],true);
            if(actual.PhysicalIdentity!=expected.Identity||actual.ResolvedLocation!=expected.ResolvedLocation)throw new IOException("FileChanged");
        }
        catch{Dispose();throw;}
    }
    private void Pin(string path)
    {
        // FILE_LIST_DIRECTORY is essential: attribute-only handles do not
        // participate in Windows read/write/delete share enforcement.
        var handle=CreateFileW(path,0x81,1,IntPtr.Zero,3,0x02000000|0x00200000|0x00100000,IntPtr.Zero);
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new IOException("DirectoryAuthorizationUnavailable",new Win32Exception(error));}
        pins.Add(handle);var info=FileAllocation.InspectMetadata(handle);
        if(info.Attributes is not {} attributes||(attributes&0x10)==0||((attributes&0x400)!=0&&!FileAllocation.IsCloudTag(info.ReparseTag??0)))throw new IOException("UnsupportedParentLink");
    }
    public void Dispose(){for(int i=pins.Count-1;i>=0;i--)pins[i].Dispose();pins.Clear();}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
}
