using System.Runtime.InteropServices;
using FolderLens.Contracts;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public static class FileAllocation
{
    public static (long? Allocated,string? PhysicalIdentity) Inspect(string path)
    {
        var result=InspectMetadata(path);return(result.Allocated,result.PhysicalIdentity);
    }
    /// <summary>Attribute-only access, shared with readers/writers/deleters. Never follows the final reparse point or recalls data.</summary>
    public static FileIdentityMetadata InspectMetadata(string path,bool resolveLocation=false)=>InspectMetadata(path,resolveLocation,false);
    internal static FileIdentityMetadata InspectMetadata(string path,bool resolveLocation,bool legacyIdentity)
    {
        using var handle=CreateFileW(path,0x80,7,IntPtr.Zero,3,0x02000000|0x00200000|0x00100000,IntPtr.Zero);
        return InspectMetadata(handle,resolveLocation,legacyIdentity);
    }
    internal static FileIdentityMetadata InspectMetadata(SafeFileHandle handle,bool resolveLocation=false,bool legacyIdentity=false)
    {
        if(handle.IsInvalid)return new(null,null,null,null,"unknown",null);
        bool hasBasic=GetBasicInformation(handle,0,out BasicInfo basic,Marshal.SizeOf<BasicInfo>());
        long? allocated=GetFileInformationByHandleEx(handle,1,out StandardInfo standard,Marshal.SizeOf<StandardInfo>()) && standard.Allocation>=0?standard.Allocation:null;
        var (identity,volume)=FileHandleIdentity.Read(handle,hasBasic?basic.Creation:0,legacyIdentity);
        string mode=GetCaseInformation(handle,23,out CaseInfo sensitivity,Marshal.SizeOf<CaseInfo>())?(sensitivity.Flags&1)!=0?"sensitive":"insensitive":"unknown";
        uint? tag=GetTagInformation(handle,9,out TagInfo tags,Marshal.SizeOf<TagInfo>())?tags.Tag:null;
        string? locator=null;
        if(resolveLocation)
        {
            var buffer=new System.Text.StringBuilder(32768);
            uint length=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,1);
            if(length==0)length=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,0);
            if(length>0&&length<buffer.Capacity)locator=buffer.ToString().TrimEnd('\\');
        }
        return new(allocated,identity,volume,hasBasic&&basic.Change>0?basic.Change:null,mode,tag){ResolvedLocation=locator,Attributes=hasBasic?basic.Attributes:null};
    }
    internal static (string? Identity,string? Volume) ReadLegacyIdentity(SafeFileHandle handle,long creation)
        =>FileHandleIdentity.Read(handle,creation,true);
    public static bool IsDeferred(long attributes)=>(attributes&(0x1000|0x40000|0x400000))!=0;
    // Cloud tags vary in bits 12..15; they are not junction/symlink tags.
    public static bool IsCloudTag(uint tag)=>(tag&0xFFFF0FFF)==0x9000001A;
    public static bool IsLinkTag(uint? tag)=>tag is 0xA0000003 or 0xA000000C;
    [StructLayout(LayoutKind.Sequential)]private struct BasicInfo{public long Creation,Access,Write,Change;public uint Attributes;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,System.Text.StringBuilder path,uint size,uint flags);
    [StructLayout(LayoutKind.Sequential)]private struct CaseInfo{public uint Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct TagInfo{public uint Attributes,Tag;}
    [StructLayout(LayoutKind.Sequential)]private struct StandardInfo{public long Allocation,EndOfFile;public uint Links;public byte DeletePending,Directory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out StandardInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetBasicInformation(SafeFileHandle handle,int kind,out BasicInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetCaseInformation(SafeFileHandle handle,int kind,out CaseInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetTagInformation(SafeFileHandle handle,int kind,out TagInfo info,int size);
}
public sealed record FileIdentityMetadata(long? Allocated,string? PhysicalIdentity,string? VolumeIdentity,long? ChangeTime,string CaseMode,uint? ReparseTag)
{
    public string? ResolvedLocation {get;init;}
    public uint? Attributes {get;init;}
}
