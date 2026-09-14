using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The launcher cannot read its command until it has joined this job. Every
// descendant is therefore owned even if the requested process exits immediately.
public sealed class FolderLensLoggedProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public FolderLensLoggedProcessJob()
    {
        handle=CreateJobObjectW(IntPtr.Zero,null);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new ExtendedLimits {Basic=new BasicLimits {Flags=0x2000}};
        if(!SetInformationJobObject(handle,9,ref limits,Marshal.SizeOf(typeof(ExtendedLimits))))
        {int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
    }
    public void Assign(Process process)
    {if(!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void Terminate()
    {if(!TerminateJobObject(handle,124))throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void Dispose(){handle.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessUserTime,JobUserTime;public uint Flags;public UIntPtr MinWorking,MaxWorking;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]private static extern SafeFileHandle CreateJobObjectW(IntPtr security,string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref ExtendedLimits info,int length);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateJobObject(SafeFileHandle job,uint code);
}
