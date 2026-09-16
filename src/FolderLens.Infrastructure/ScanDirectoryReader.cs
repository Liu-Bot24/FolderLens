using System.ComponentModel;
using System.Runtime.InteropServices;
using FolderLens.Core;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record ScanEntry(string Name,bool Directory,long Bytes,long Modified,long Created,long Attributes,
    string Hydration,string? SkipReason,long? Allocated,string? PhysicalIdentity,long? ChangeTime,string CaseMode="unknown");
public sealed record ScanDirectoryPacket(string State,ScanEntry[] Entries,string? ErrorCode=null,
    string? PhysicalIdentity=null,string? VolumeIdentity=null,string CaseMode="unknown",SourceFileStamp? FileStamp=null,string? ResolvedLocation=null,ScanEntry? FileObservation=null);

/// <summary>One directory only; its caller owns the persistent disk-backed traversal queue.</summary>
public static class ScanDirectoryReader
{
    public static IEnumerable<ScanDirectoryPacket> Read(string directory,bool allowCloud=false)
    {
        directory=PathRules.ValidateSource(directory);
        FileAttributes attributes=default;bool missing=false;
        try{attributes=File.GetAttributes(directory);}
        catch(FileNotFoundException){missing=true;}
        catch(DirectoryNotFoundException){missing=true;}
        if(missing)
        {
            // Absence is conclusive only with an accessible, ordinary parent on a known volume.
            string? parent=Path.GetDirectoryName(directory.TrimEnd('\\'));
            var observed=parent is null?null:ScanPathProbe.Read(parent);
            if(observed?.State=="present"&&observed.FileStamp is null&&observed.VolumeIdentity is not null)
                yield return new("completed",[],"DirectoryMissing",VolumeIdentity:observed.VolumeIdentity);
            else yield return new("offline",[],"ParentUnavailable");
            yield break;
        }
        if((attributes&FileAttributes.Directory)==0)throw new IOException("Not a directory.");
        if(!allowCloud && FileAllocation.IsDeferred((long)attributes))
        {yield return new("excluded",[],"DeferredOffline");yield break;}
        using var directoryHandle=CreateFileW(LongPath(directory),0x81,7,IntPtr.Zero,3,0x02000000|0x00200000|0x00100000,IntPtr.Zero);
        if(directoryHandle.IsInvalid)throw FileError(Marshal.GetLastWin32Error());
        var identity=FileAllocation.InspectMetadata(directoryHandle,resolveLocation:true);
        if(identity.Attributes is not {} actualAttributes||(actualAttributes&0x10)==0)throw new IOException("DirectoryChanged");
        if(!allowCloud&&FileAllocation.IsDeferred(actualAttributes)){yield return new("excluded",[],"DeferredOffline");yield break;}
        if((actualAttributes&0x400)!=0&&(identity.ReparseTag is null||!FileAllocation.IsCloudTag(identity.ReparseTag.Value)))
        {yield return new("excluded",[],"LinkDirectorySkipped");yield break;}
        void VerifyLocation()
        {
            var current=FileAllocation.InspectMetadata(directory,resolveLocation:true);
            if(identity.PhysicalIdentity is null||current.PhysicalIdentity!=identity.PhysicalIdentity||current.ResolvedLocation!=identity.ResolvedLocation)
                throw new IOException("DirectoryChanged");
        }
        VerifyLocation();
        yield return new("started",[],PhysicalIdentity:identity.PhysicalIdentity,VolumeIdentity:identity.VolumeIdentity,CaseMode:identity.CaseMode,ResolvedLocation:identity.ResolvedLocation);
        // Both identity and every entry's attributes/file ID come from this open
        // directory, never from a fresh child path that could now name a replacement.
        const int capacity=64*1024;IntPtr buffer=Marshal.AllocHGlobal(capacity);
        try
        {
            bool extended=true;var batch=new List<ScanEntry>(128);
            while(true)
            {
                VerifyLocation();
                if(!ReadDirectoryInformation(directoryHandle,extended?19:14,buffer,capacity))
                {
                    int error=Marshal.GetLastWin32Error();
                    if(error==18)break;
                    // Providers without extended IDs still return coherent metadata
                    // through the same directory handle; unknown identity stays unknown.
                    if(extended&&error is 50 or 87){extended=false;continue;}
                    throw FileError(error);
                }
                int offset=0;
                while(true)
                {
                    int nameOffset=extended?88:68;
                    if(offset<0||offset>capacity-nameOffset)throw new IOException("InvalidDirectoryRecord");
                    IntPtr record=IntPtr.Add(buffer,offset);int next=Marshal.ReadInt32(record),nameBytes=Marshal.ReadInt32(record,60);
                    if(nameBytes<0||(nameBytes&1)!=0||nameBytes>capacity-offset-nameOffset)throw new IOException("InvalidDirectoryRecord");
                    string name=Marshal.PtrToStringUni(IntPtr.Add(record,nameOffset),nameBytes/2)!;
                    if(name is not ("." or ".."))
                    {
                        uint flags=unchecked((uint)Marshal.ReadInt32(record,56));bool isDirectory=(flags&0x10)!=0,deferred=FileAllocation.IsDeferred(flags),reparse=(flags&0x400)!=0;
                        uint? tag=extended?unchecked((uint)Marshal.ReadInt32(record,68)):null;
                        bool cloud=reparse&&tag is {} value&&FileAllocation.IsCloudTag(value);
                        string? skip=reparse&&!cloud?isDirectory?"LinkDirectorySkipped":"LinkFileSkipped":!allowCloud&&deferred&&isDirectory?"DeferredOffline":null;
                        long creation=Marshal.ReadInt64(record,8),write=Marshal.ReadInt64(record,24),change=Marshal.ReadInt64(record,32),bytes=Marshal.ReadInt64(record,40),allocated=Marshal.ReadInt64(record,48);
                        if(bytes<0)throw new IOException("InvalidDirectoryLength");
                        string? fileIdentity=extended&&identity.VolumeIdentity is {} volume&&creation>0&&!deferred&&skip is null
                            ?$"{volume}:{unchecked((ulong)Marshal.ReadInt64(record,72)):X16}{unchecked((ulong)Marshal.ReadInt64(record,80)):X16}:{creation:X16}":null;
                        batch.Add(new(name,isDirectory,isDirectory?0:bytes,ToTicks(write),ToTicks(creation),flags,deferred?"placeholder":"local",skip,
                            deferred||skip is not null||allocated<0?null:allocated,fileIdentity,deferred||skip is not null||change<=0?null:change));
                        if(batch.Count==128){VerifyLocation();yield return new("batch",batch.ToArray());batch.Clear();}
                    }
                    if(next==0)break;
                    if(next<nameOffset+nameBytes||next>capacity-offset)throw new IOException("InvalidDirectoryRecord");
                    offset+=next;
                }
                if(batch.Count>0){VerifyLocation();yield return new("batch",batch.ToArray());batch.Clear();}
            }
            VerifyLocation();yield return new("completed",[]);
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    private static long ToTicks(long value)=>DateTime.FromFileTimeUtc(value).Ticks;
    private static Exception FileError(int error)=>error==5?new UnauthorizedAccessException("Directory access denied."):
        new IOException("Directory enumeration failed.",new Win32Exception(error));
    private static string LongPath(string value)=>value.StartsWith(@"\\?\",StringComparison.Ordinal)?value:
        value.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+value[2..]:@"\\?\"+value;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ReadDirectoryInformation(SafeFileHandle handle,int kind,IntPtr buffer,int size);
}
