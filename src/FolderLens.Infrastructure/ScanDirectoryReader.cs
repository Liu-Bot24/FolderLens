using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FolderLens.Core;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record ScanEntry(string Name,bool Directory,long Bytes,long Modified,long Created,long Attributes,
    string Hydration,string? SkipReason,long? Allocated,string? PhysicalIdentity,long? ChangeTime,string CaseMode="unknown");
public sealed record ScanDirectoryPacket(string State,ScanEntry[] Entries,string? ErrorCode=null,
    string? PhysicalIdentity=null,string? VolumeIdentity=null,string CaseMode="unknown",SourceFileStamp? FileStamp=null);

/// <summary>One directory only; its caller owns the persistent disk-backed traversal queue.</summary>
public static class ScanDirectoryReader
{
    public static IEnumerable<ScanDirectoryPacket> Read(string directory,bool allowCloud=false)
    {
        directory=PathRules.ValidateSource(directory);
        FileAttributes attributes=File.GetAttributes(directory);
        if((attributes&FileAttributes.Directory)==0)throw new IOException("Not a directory.");
        if(!allowCloud && FileAllocation.IsDeferred((long)attributes))
        {yield return new("excluded",[],"DeferredOffline");yield break;}
        var identity=FileAllocation.InspectMetadata(directory);
        if((attributes&FileAttributes.ReparsePoint)!=0 && (identity.ReparseTag is null || !FileAllocation.IsCloudTag(identity.ReparseTag.Value)))
        {yield return new("excluded",[],"LinkDirectorySkipped");yield break;}
        yield return new("started",[],PhysicalIdentity:identity.PhysicalIdentity,VolumeIdentity:identity.VolumeIdentity,CaseMode:identity.CaseMode);
        string pattern=Path.Combine(LongPath(directory),"*");
        using var search=FindFirstFileExW(pattern,1,out var item,0,IntPtr.Zero,2);
        if(search.IsInvalid)
        {
            int error=Marshal.GetLastWin32Error();
            if(error==2){yield return new("completed",[]);yield break;}
            throw FileError(error);
        }
        var batch=new List<ScanEntry>(128);var flush=Stopwatch.StartNew();
        do
        {
            if(item.Name is "." or "..")continue;
            bool isDirectory=(item.Attributes&0x10)!=0;
            bool deferred=FileAllocation.IsDeferred(item.Attributes);
            bool reparse=(item.Attributes&0x400)!=0;
            bool cloud=reparse && FileAllocation.IsCloudTag(item.ReparseTag);
            string? skip=reparse&&!cloud?isDirectory?"LinkDirectorySkipped":"LinkFileSkipped":!allowCloud&&deferred&&isDirectory?"DeferredOffline":null;
            FileIdentityMetadata? metadata=null;
            // Enumeration attributes suffice for placeholders; even attribute opens can fetch virtual items.
            if(skip is null && !deferred)metadata=FileAllocation.InspectMetadata(Path.Combine(directory,item.Name));
            ulong bytes=((ulong)item.SizeHigh<<32)|item.SizeLow;
            if(bytes>long.MaxValue)throw new IOException("File length is outside the supported range.");
            batch.Add(new(item.Name,isDirectory,isDirectory?0:(long)bytes,ToTicks(item.Write),ToTicks(item.Creation),item.Attributes,
                deferred?"placeholder":"local",skip,metadata?.Allocated,metadata?.PhysicalIdentity,metadata?.ChangeTime,metadata?.CaseMode??"unknown"));
            if(batch.Count==128 || flush.ElapsedMilliseconds>=50){yield return new("batch",batch.ToArray());batch.Clear();flush.Restart();}
        }while(FindNextFileW(search,out item));
        int finalError=Marshal.GetLastWin32Error();
        // Preserve observed entries even if FindNext fails; only the completed packet authorizes reconciliation.
        if(batch.Count>0)yield return new("batch",batch.ToArray());
        if(finalError!=18)throw FileError(finalError);
        yield return new("completed",[]);
    }
    private static long ToTicks(long value)=>DateTime.FromFileTimeUtc(value).Ticks;
    private static Exception FileError(int error)=>error==5?new UnauthorizedAccessException("Directory access denied."):
        new IOException("Directory enumeration failed.",new Win32Exception(error));
    private static string LongPath(string value)=>value.StartsWith(@"\\?\",StringComparison.Ordinal)?value:
        value.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+value[2..]:@"\\?\"+value;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode,Pack=4)]private struct FindData
    {
        public uint Attributes;public long Creation,Access,Write;public uint SizeHigh,SizeLow,ReparseTag,Reserved;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)]public string Alternate;
    }
    private sealed class FindHandle:SafeHandleZeroOrMinusOneIsInvalid
    {private FindHandle():base(true){}protected override bool ReleaseHandle()=>FindClose(handle);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern FindHandle FindFirstFileExW(string file,int level,out FindData data,int search,IntPtr filter,int flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool FindNextFileW(FindHandle handle,out FindData data);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool FindClose(IntPtr handle);
}
