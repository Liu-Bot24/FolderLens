using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record SourceImageAuthorization(string Path,SourceFileStamp Stamp,long[] Handles);

/// <summary>Owns the authorized image and its directory chain through the last
/// source read. Duplicated handles survive retirement of the probing worker.</summary>
public sealed class SourceImageLease : IDisposable
{
    private readonly List<SafeFileHandle> pins;
    public string Path {get;}
    public SourceFileStamp Stamp {get;}
    internal SourceImageLease(string path,SourceFileStamp stamp,List<SafeFileHandle> pins)
    {Path=path;Stamp=stamp;this.pins=pins;}
    internal SourceImageAuthorization Export()=>new(Path,Stamp,pins.Select(pin=>pin.DangerousGetHandle().ToInt64()).ToArray());
    internal static SourceImageLease Duplicate(Process source,SourceImageAuthorization authorization)
    {
        authorization.Stamp.Validate();
        if(string.IsNullOrEmpty(authorization.Stamp.SourceSignature)||authorization.Handles.Length is <2 or >128||
            authorization.Handles.Any(handle=>handle is 0 or -1))throw new InvalidDataException("Invalid image authorization.");
        string path=FolderLens.Core.PathRules.ValidateSource(authorization.Path);
        var handles=new List<SafeFileHandle>();
        try
        {
            foreach(long remote in authorization.Handles)
            {
                if(!DuplicateHandle(source.SafeHandle,(nint)remote,GetCurrentProcess(),out var local,0,false,2))
                {int error=Marshal.GetLastWin32Error();local?.Dispose();throw new Win32Exception(error);}
                handles.Add(local);
            }
            return new(path,authorization.Stamp,handles);
        }
        catch{foreach(var handle in handles)handle.Dispose();throw;}
    }
    public void Dispose(){for(int i=pins.Count-1;i>=0;i--)pins[i].Dispose();pins.Clear();}
    [DllImport("kernel32.dll")]private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess,nint sourceHandle,nint targetProcess,out SafeFileHandle targetHandle,uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint options);
}
