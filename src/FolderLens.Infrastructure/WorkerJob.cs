using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

internal sealed class WorkerJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private int? processId;
    private static readonly object aggregateSync=new();
    private static SafeFileHandle? aggregate;
    private static long aggregateLimit;
    public WorkerJob(long memoryLimit)
    {
        handle=CreateJobObjectW(IntPtr.Zero,null);if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var info=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000|0x100|0x200},ProcessMemoryLimit=(UIntPtr)memoryLimit,JobMemoryLimit=(UIntPtr)memoryLimit};
        if(!SetInformationJobObject(handle,9,ref info,Marshal.SizeOf<ExtendedLimits>())){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
    }
    public void Assign(Process process)
    {
        EnsureAggregateLimit();
        try
        {
            lock(aggregateSync)if(!AssignProcessToJobObject(aggregate!,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            processId=process.Id;WorkerResources.Shared.Register(process);
        }
        catch{if(!process.HasExited)process.Kill(entireProcessTree:true);throw;}
    }
    internal static void EnsureAggregateLimit()
    {
        lock(aggregateSync)
            if(aggregate is null)SetAggregateLimit(Math.Max(128L<<20,WorkerResources.Shared.Snapshot.HardLimitBytes/2));
    }
    internal static long AggregateLimit {get{lock(aggregateSync)return aggregateLimit;}}
    internal static void SetAggregateLimit(long memoryLimit,bool allowIncrease=true)
    {
        lock(aggregateSync)
        {
            if(aggregate is not null&&!allowIncrease)memoryLimit=Math.Min(memoryLimit,aggregateLimit);
            aggregate??=CreateJobObjectW(IntPtr.Zero,null);if(aggregate.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits=new ExtendedLimits{Basic=new BasicLimits{Flags=0x2000|0x200|0x8,ActiveLimit=32},JobMemoryLimit=(UIntPtr)memoryLimit};
            if(!SetInformationJobObject(aggregate,9,ref limits,Marshal.SizeOf<ExtendedLimits>()))throw new Win32Exception(Marshal.GetLastWin32Error());
            aggregateLimit=memoryLimit;
        }
    }
    public void Dispose(){handle.Dispose();if(processId is {} pid){processId=null;WorkerResources.Shared.Unregister(pid);}}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessUserTime,JobUserTime;public uint Flags;public UIntPtr MinWorking,MaxWorking;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]private static extern SafeFileHandle CreateJobObjectW(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref ExtendedLimits info,int length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
}
