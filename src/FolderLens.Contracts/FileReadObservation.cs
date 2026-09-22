using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Contracts;

/// <summary>Metadata of the actual open file, never a content hash or a UI-thread path probe.</summary>
public sealed record FileReadObservation(long Length,long ModifiedUtcTicks,string Signature)
{
    public static FileReadObservation Read(string path)
    {
        using var handle=CreateFileW(path,0x80,7,IntPtr.Zero,3,0x02000000|0x00200000|0x00100000,IntPtr.Zero);
        if(handle.IsInvalid)throw new IOException("SourceIoError",new Win32Exception(Marshal.GetLastWin32Error()));
        return Read(handle);
    }
    public static FileReadObservation Read(SafeFileHandle handle)=>Read(handle,false);
    internal static FileReadObservation Read(SafeFileHandle handle,bool legacyIdentity)
    {
        if(!GetBasic(handle,0,out BasicInfo basic,Marshal.SizeOf<BasicInfo>())||!GetStandard(handle,1,out StandardInfo standard,Marshal.SizeOf<StandardInfo>()))
            throw new IOException("SourceIoError",new Win32Exception(Marshal.GetLastWin32Error()));
        string? identity=FileHandleIdentity.Read(handle,basic.Creation,legacyIdentity).Identity;
        long modified=DateTime.FromFileTimeUtc(basic.Write).Ticks,created=DateTime.FromFileTimeUtc(basic.Creation).Ticks;
        long? change=basic.Change>0?basic.Change:null;
        return new(standard.EndOfFile,modified,$"{standard.EndOfFile}:{modified}:{created}:{change}:{identity}:{basic.Attributes}");
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicInfo{public long Creation,Access,Write,Change;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct StandardInfo{public long Allocation,EndOfFile;public uint Links;public byte DeletePending,Directory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetBasic(SafeFileHandle handle,int kind,out BasicInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetStandard(SafeFileHandle handle,int kind,out StandardInfo info,int size);
}
