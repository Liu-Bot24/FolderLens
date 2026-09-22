using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Contracts;

/// <summary>One identity format for directory scans, source probes and decoder handles.</summary>
public static class FileHandleIdentity
{
    public static (string? Identity,string? Volume) Read(SafeFileHandle handle,long creation,bool legacyOnly=false)
    {
        // File IDs may be reused: the creation time belongs to the identity too.
        if(creation<=0||handle.IsInvalid)return(null,null);
        if(!legacyOnly&&GetIdentity(handle,18,out IdInfo id,Marshal.SizeOf<IdInfo>()))
        {
            string volume=$"{id.Volume:X16}";
            return($"{volume}:{id.Low:X16}{id.High:X16}:{creation:X16}",volume);
        }
        // Some SMB servers implement only the 64-bit index. Keep the same handle
        // and verify its creation time; paths and timestamps alone are not IDs.
        if(!GetFileInformationByHandle(handle,out LegacyInfo info))return(null,null);
        ulong index=((ulong)info.IndexHigh<<32)|info.IndexLow;
        long observedCreation=unchecked((long)(((ulong)info.CreationHigh<<32)|info.CreationLow));
        if(index==0||observedCreation!=creation)return(null,null);
        string legacyVolume=$"legacy:{info.Volume:X8}";
        return($"{legacyVolume}:{index:X16}:{creation:X16}",legacyVolume);
    }

    [StructLayout(LayoutKind.Sequential)]private struct IdInfo{public ulong Volume,Low,High;}
    [StructLayout(LayoutKind.Sequential)]private struct LegacyInfo
    {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetIdentity(SafeFileHandle handle,int kind,out IdInfo info,int size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out LegacyInfo info);
}
