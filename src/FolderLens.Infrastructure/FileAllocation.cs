using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public static class FileAllocation
{
    public static (long? Allocated,string? PhysicalIdentity) Inspect(string path)
    {
        var result=InspectMetadata(path);return(result.Allocated,result.PhysicalIdentity);
    }
    /// <summary>Attribute-only access, shared with readers/writers/deleters. Never follows the final reparse point or recalls data.</summary>
    public static FileIdentityMetadata InspectMetadata(string path)
    {
        using var handle=CreateFileW(path,0x80,7,IntPtr.Zero,3,0x02000000|0x00200000|0x00100000,IntPtr.Zero);
        if(handle.IsInvalid)return new(null,null,null,null,"unknown",null);
        bool hasBasic=GetBasicInformation(handle,0,out BasicInfo basic,Marshal.SizeOf<BasicInfo>());
        long? allocated=GetFileInformationByHandleEx(handle,1,out StandardInfo standard,Marshal.SizeOf<StandardInfo>()) && standard.Allocation>=0?standard.Allocation:null;
        string? identity=null,volume=null;
        // A file ID can be reused after deletion. Creation identity must participate in the cache identity.
        if(hasBasic && basic.Creation>0 && GetFileIdInformation(handle,18,out IdInfo id,Marshal.SizeOf<IdInfo>()))
        {volume=$"{id.Volume:X16}";identity=$"{volume}:{id.Low:X16}{id.High:X16}:{basic.Creation:X16}";}
        string mode=GetCaseInformation(handle,23,out CaseInfo sensitivity,Marshal.SizeOf<CaseInfo>())?(sensitivity.Flags&1)!=0?"sensitive":"insensitive":"unknown";
        uint? tag=GetTagInformation(handle,9,out TagInfo tags,Marshal.SizeOf<TagInfo>())?tags.Tag:null;
        return new(allocated,identity,volume,hasBasic&&basic.Change>0?basic.Change:null,mode,tag);
    }
    public static bool IsDeferred(long attributes)=>(attributes&(0x1000|0x40000|0x400000))!=0;
    // Cloud tags vary in bits 12..15; they are not junction/symlink tags.
    public static bool IsCloudTag(uint tag)=>(tag&0xFFFF0FFF)==0x9000001A;
    public static bool IsLinkTag(uint? tag)=>tag is 0xA0000003 or 0xA000000C;
    [StructLayout(LayoutKind.Sequential)]private struct BasicInfo{public long Creation,Access,Write,Change;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct CaseInfo{public uint Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct TagInfo{public uint Attributes,Tag;}
    [StructLayout(LayoutKind.Sequential)]private struct StandardInfo{public long Allocation,EndOfFile;public uint Links;public byte DeletePending,Directory;}
    [StructLayout(LayoutKind.Sequential)]private struct IdInfo{public ulong Volume,Low,High;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out StandardInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileIdInformation(SafeFileHandle handle,int kind,out IdInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetBasicInformation(SafeFileHandle handle,int kind,out BasicInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetCaseInformation(SafeFileHandle handle,int kind,out CaseInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetTagInformation(SafeFileHandle handle,int kind,out TagInfo info,int size);
}
public sealed record FileIdentityMetadata(long? Allocated,string? PhysicalIdentity,string? VolumeIdentity,long? ChangeTime,string CaseMode,uint? ReparseTag);
