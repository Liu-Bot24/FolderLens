using System.ComponentModel;
using System.Runtime.InteropServices;
using FolderLens.Contracts;
using FolderLens.Core;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

internal static class SourceReadPreparation
{
    // Executed only in the owned, cancellable scan process. Metadata access
    // does not download a placeholder; hydration requires explicit approval.
    internal static ScanDirectoryPacket Read(string path,SourceFileStamp expected,bool allowCloud)
    {
        path=PathRules.ValidateSource(path);expected.Validate();
        var packet=ScanPathProbe.Read(path,allowCloud);
        if(packet.State!="present"||packet.FileObservation is not {} before)return packet;
        var scanned=SourceObservationSignature.Parse(expected.SourceSignature??throw new InvalidDataException("Missing source observation."));
        var complete=SourceObservationSignature.Parse(FileObservationWriter.Signature(before));
        if(expected.Length!=before.Bytes||expected.ModifiedUtcTicks!=before.Modified||!scanned.CanCompleteWith(complete))throw new IOException("FileChanged");
        if(before.Hydration!="placeholder")return packet;
        if(!allowCloud)throw new IOException("CloudReadNotApproved");
        using var handle=CreateFileW(path,0x80,5,IntPtr.Zero,3,0x02000000|0x00200000|0x00100000,IntPtr.Zero);
        if(handle.IsInvalid)throw new IOException("SourceIoError",new Win32Exception(Marshal.GetLastWin32Error()));
        var metadata=FileAllocation.InspectMetadata(handle);
        if(!FileAllocation.IsCloudTag(metadata.ReparseTag??0))throw new IOException("UnsupportedCloudProvider");
        if(FileReadObservation.Read(handle).Signature!=FileObservationWriter.Signature(before))throw new IOException("FileChanged");
        // Retain the same file handle, excluding concurrent writers. cfapi
        // materializes the approved file without loading it into application RAM.
        Marshal.ThrowExceptionForHR(CfHydratePlaceholder(handle,0,-1,0,IntPtr.Zero));
        var after=FileReadObservation.Read(handle);
        if(!complete.SameFileAfterHydration(SourceObservationSignature.Parse(after.Signature)))throw new IOException("FileChanged");
        packet=ScanPathProbe.Read(path,true);
        if(packet.FileObservation is not {} ready||FileObservationWriter.Signature(ready)!=after.Signature||ready.Hydration!="local")throw new IOException("FileChanged");
        return packet;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("cldapi.dll")]private static extern int CfHydratePlaceholder(SafeFileHandle file,long offset,long length,int flags,IntPtr overlapped);
}
