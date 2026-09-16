using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public enum WorkerPriority { Foreground=0,Visible=1,Prefetch=2,Metadata=3 }
public enum WorkerLane { General, VideoCover }
public readonly record struct SourceFileStamp(long Length,long ModifiedUtcTicks,string? SourceSignature=null)
{
    public void Validate(){if(Length<0||ModifiedUtcTicks<0||ModifiedUtcTicks>DateTime.MaxValue.Ticks)throw new ArgumentException("源文件版本戳无效。");}
}
public sealed class WorkerResourceLimitException(string message):IOException(message);
public sealed record WorkerResourceSnapshot(long ProcessTreeBytes,long AvailablePhysicalBytes,long SoftLimitBytes,long HardLimitBytes,int ActiveForeground,int ActiveBackground,int Pending,int CpuThreads,bool UnderPressure,bool MeasurementComplete);

/// <summary>One application-wide admission queue. Worker jobs enforce the hard memory
/// cap in the kernel; this queue reserves CPU slots and prioritizes visible work.</summary>
public sealed class WorkerResources
{
    public static WorkerResources Shared {get;}=new();
    private readonly object sync=new();
    private readonly List<Waiter> pending=[];
    private readonly HashSet<Lease> active=[];
    private readonly Dictionary<int,Process> registered=[];
    private readonly Timer? monitor;
    private WorkerResourceSnapshot snapshot;
    private int monitoring;
    private readonly int cpuThreads;
    public int ThumbnailConcurrency { get; }
    public event Action? MemoryPressure;
    private WorkerResources():this(InitialSnapshot())
    {
        monitor=new(_=>Refresh(),null,TimeSpan.Zero,TimeSpan.FromSeconds(1));
    }
    // Deterministic admission verification without sharing leases or native-memory
    // sampling with unrelated tests. Production always uses the monitored Shared.
    internal WorkerResources(WorkerResourceSnapshot initial)
    {
        if(initial.CpuThreads is <1 or >16||initial.ActiveForeground!=0||initial.ActiveBackground!=0||initial.Pending!=0)
            throw new ArgumentException("Invalid initial worker budget.",nameof(initial));
        cpuThreads=initial.CpuThreads;snapshot=initial;
        int backgroundCapacity=Math.Min(cpuThreads/2,Math.Max(2,cpuThreads-4));
        ThumbnailConcurrency=Math.Clamp(Math.Min(backgroundCapacity,(int)Math.Min(8,initial.SoftLimitBytes/(512L<<20))),2,8);
    }
    private static WorkerResourceSnapshot InitialSnapshot()
    {
        var memory=PhysicalMemory();return new(0,memory.Available,Math.Min(6L<<30,memory.Total/8),Math.Min(8L<<30,memory.Total/4),0,0,0,Math.Min(16,Math.Max(2,Environment.ProcessorCount/2)),false,true);
    }
    public WorkerResourceSnapshot Snapshot {get{lock(sync)return snapshot;}}
    // The timer owns sampling. Worker start/stop must never enumerate processes on
    // a decoder's critical path; the kernel job cap stays in force between samples.
    internal void Register(Process process){lock(sync)registered[process.Id]=process;}
    internal void Unregister(int pid){lock(sync)registered.Remove(pid);}
    public async Task<Lease> Acquire(WorkerPriority priority,CancellationToken cancellation=default,WorkerLane lane=WorkerLane.General)
    {
        if(!Enum.IsDefined(priority)||!Enum.IsDefined(lane))throw new ArgumentOutOfRangeException(nameof(priority));
        cancellation.ThrowIfCancellationRequested();var waiter=new Waiter(priority,cancellation,lane);
        lock(sync)
        {
            if(pending.Count>=256)throw new WorkerResourceLimitException("媒体任务队列已满，请稍后重试。");
            pending.Add(waiter);waiter.Registration=cancellation.Register(()=>Cancel(waiter));Dispatch();
        }
        try{return await waiter.Completion.Task.ConfigureAwait(false);}
        finally{waiter.Registration.Dispose();}
    }
    private void Cancel(Waiter waiter)
    {
        lock(sync){if(pending.Remove(waiter))waiter.Completion.TrySetCanceled(waiter.Cancellation);Dispatch();}
    }
    private void Dispatch()
    {
        while(pending.Count>0)
        {
            // A busy foreground slot must not idle an independent background slot.
            // Rank eligible work, retaining FIFO within each priority, rather than
            // stopping at the first waiter whose own resources are occupied.
            foreach(var canceled in pending.Where(w=>w.Cancellation.IsCancellationRequested).ToArray())
            {pending.Remove(canceled);canceled.Completion.TrySetCanceled(canceled.Cancellation);}
            int foregroundCount=active.Count(a=>a.Priority==WorkerPriority.Foreground),backgroundCount=active.Count-foregroundCount;
            int usedThreads=active.Sum(a=>a.CpuThreads),foregroundThreads=Math.Min(4,cpuThreads);
            int Threads(WorkerPriority priority)=>priority==WorkerPriority.Foreground?foregroundThreads:Math.Max(1,(cpuThreads-foregroundThreads)/ThumbnailConcurrency);
            bool drainingForForeground=foregroundCount==0&&usedThreads+foregroundThreads>cpuThreads&&pending.Any(waiter=>waiter.Priority==WorkerPriority.Foreground&&(waiter.Lane!=WorkerLane.VideoCover||!active.Any(a=>a.Lane==WorkerLane.VideoCover)));
            bool CanRun(Waiter waiter)
            {
                if(waiter.Priority!=WorkerPriority.Foreground&&drainingForForeground)return false;
                if(waiter.Lane==WorkerLane.VideoCover&&active.Any(a=>a.Lane==WorkerLane.VideoCover))return false;
                if(waiter.Priority==WorkerPriority.Foreground?foregroundCount>=1:backgroundCount>=ThumbnailConcurrency||snapshot.UnderPressure)return false;
                return usedThreads+Threads(waiter.Priority)<=cpuThreads;
            }
            Waiter? next=pending.Where(CanRun).OrderBy(w=>w.Priority).FirstOrDefault();
            if(next is null)break;
            if(next.Cancellation.IsCancellationRequested){pending.Remove(next);next.Completion.TrySetCanceled(next.Cancellation);continue;}
            bool foreground=next.Priority==WorkerPriority.Foreground;
            if(foreground&&(snapshot.AvailablePhysicalBytes<256L<<20||snapshot.ProcessTreeBytes>=snapshot.HardLimitBytes))
            {
                pending.Remove(next);next.Completion.TrySetException(new WorkerResourceLimitException("可用内存不足，已暂停新的重解码任务。"));continue;
            }
            int threads=Threads(next.Priority);
            pending.Remove(next);var lease=new Lease(this,next.Priority,threads,next.Lane);active.Add(lease);next.Completion.TrySetResult(lease);
        }
        snapshot=snapshot with{ActiveForeground=active.Count(a=>a.Priority==WorkerPriority.Foreground),ActiveBackground=active.Count(a=>a.Priority!=WorkerPriority.Foreground),Pending=pending.Count};
    }
    private void Release(Lease lease){lock(sync){active.Remove(lease);Dispatch();}}
    private void Refresh()
    {
        if(Interlocked.Exchange(ref monitoring,1)!=0)return;
        try
        {
            Process[] workers;lock(sync)workers=registered.Values.ToArray();
            var memory=PhysicalMemory();long total=0,workerBytes=0;bool complete=true;var measured=new HashSet<int>();
            var workerIds=workers.Select(p=>p.Id).ToHashSet();
            void Measure(Process process)
            {
                try{if(!measured.Add(process.Id)||process.HasExited)return;long bytes=ReadPrivateBytes(process);total=checked(total+bytes);if(workerIds.Contains(process.Id))workerBytes=checked(workerBytes+bytes);}catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){complete=false;}
            }
            using(var app=Process.GetCurrentProcess())Measure(app);foreach(var process in workers)Measure(process);
            foreach(int pid in OwnedDescendants(workers.Select(p=>{try{return p.Id;}catch(InvalidOperationException){return -1;}}).ToHashSet()))
            {try{using var process=Process.GetProcessById(pid);Measure(process);}catch(ArgumentException){}}
            long soft=Math.Min(6L<<30,memory.Total/8),hard=Math.Min(8L<<30,memory.Total/4),safety=Math.Max(512L<<20,memory.Total/32);
            bool pressure=total>=soft||memory.Available<safety;
            ObserveMemory(total,memory.Available,soft,hard,pressure,complete);
            // An incomplete sample may tighten the budget but must not expand it.
            WorkerJob.SetAggregateLimit(Math.Max(128L<<20,hard-Math.Max(0,total-workerBytes)),allowIncrease:complete);
        }
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        {
            lock(sync)snapshot=snapshot with{MeasurementComplete=false,UnderPressure=true};
        }
        finally{Volatile.Write(ref monitoring,0);}
    }
    internal void ObserveMemory(long total,long available,long soft,long hard,bool pressure,bool complete)
    {
        Lease[] cancel;
        lock(sync)
        {
            snapshot=new(total,available,soft,hard,active.Count(a=>a.Priority==WorkerPriority.Foreground),active.Count(a=>a.Priority!=WorkerPriority.Foreground),pending.Count,cpuThreads,pressure,complete);
            cancel=pressure?active.Where(a=>a.Priority>=WorkerPriority.Prefetch).ToArray():[];Dispatch();
        }
        foreach(var lease in cancel)lease.RequestPressureCancellation();
        if(pressure)MemoryPressure?.Invoke();
    }
    internal static long ReadPrivateBytes(Process process)
    {
        var counters=new ProcessMemoryCounters{Size=(uint)Marshal.SizeOf<ProcessMemoryCounters>()};
        if(!GetProcessMemoryInfo(process.Handle,ref counters,counters.Size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return checked((long)counters.PrivateUsage.ToUInt64());
    }
    public sealed class Lease : IDisposable
    {
        private WorkerResources? owner;
        private readonly CancellationTokenSource pressure=new();
        internal Lease(WorkerResources owner,WorkerPriority priority,int cpu,WorkerLane lane){this.owner=owner;Priority=priority;CpuThreads=cpu;Lane=lane;}
        public WorkerPriority Priority {get;}
        public WorkerLane Lane {get;}
        public int CpuThreads {get;}
        public CancellationToken PressureCancellation=>pressure.Token;
        internal void RequestPressureCancellation(){try{pressure.Cancel();}catch(ObjectDisposedException){}}
        public void Dispose(){Interlocked.Exchange(ref owner,null)?.Release(this);pressure.Dispose();}
    }
    private sealed class Waiter(WorkerPriority priority,CancellationToken cancellation,WorkerLane lane)
    {
        public WorkerPriority Priority {get;}=priority;public CancellationToken Cancellation {get;}=cancellation;
        public WorkerLane Lane {get;}=lane;
        public TaskCompletionSource<Lease> Completion {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }
    private static (long Total,long Available) PhysicalMemory()
    {
        var status=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(!GlobalMemoryStatusEx(ref status))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return(checked((long)status.TotalPhysical),checked((long)status.AvailablePhysical));
    }
    private static IEnumerable<int> OwnedDescendants(HashSet<int> workerRoots)
    {
        using var capture=CreateToolhelp32Snapshot(2,0);if(capture.IsInvalid)yield break;
        var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};var rows=new List<(int Pid,int Parent,string Name)>();
        if(Process32FirstW(capture,ref entry))do{rows.Add(((int)entry.ProcessId,(int)entry.ParentProcessId,entry.Executable));}while(Process32NextW(capture,ref entry));
        int app=Environment.ProcessId;var allApp=new HashSet<int>{app};bool changed;
        do{changed=false;foreach(var row in rows)if(allApp.Contains(row.Parent)&&allApp.Add(row.Pid))changed=true;}while(changed);
        do{changed=false;foreach(var row in rows)if(workerRoots.Contains(row.Parent)&&workerRoots.Add(row.Pid))changed=true;}while(changed);
        foreach(var row in rows)if(workerRoots.Contains(row.Pid)||(allApp.Contains(row.Pid)&&row.Name.Equals("msedgewebview2.exe",StringComparison.OrdinalIgnoreCase)))yield return row.Pid;
    }
    [StructLayout(LayoutKind.Sequential)]private struct MemoryStatus{public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessMemoryCounters
    {public uint Size,PageFaultCount;public UIntPtr PeakWorkingSetSize,WorkingSetSize,QuotaPeakPagedPoolUsage,QuotaPagedPoolUsage,QuotaPeakNonPagedPoolUsage,QuotaNonPagedPoolUsage,PagefileUsage,PeakPagefileUsage,PrivateUsage;}
    [DllImport("psapi.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetProcessMemoryInfo(IntPtr process,ref ProcessMemoryCounters counters,uint size);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ProcessEntry{public uint Size,Usage,ProcessId;public UIntPtr DefaultHeap;public uint ModuleId,Threads,ParentProcessId;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Executable;}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags,uint process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool Process32FirstW(SafeFileHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool Process32NextW(SafeFileHandle snapshot,ref ProcessEntry entry);
}
