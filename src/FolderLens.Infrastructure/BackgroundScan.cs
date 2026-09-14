using FolderLens.Core;

namespace FolderLens.Infrastructure;

/// <summary>An application-owned scan survives browser navigation. The shared slots bound active root scans.</summary>
public sealed class BackgroundScan : IDisposable
{
    public string RootId {get;}
    public string Path {get;}
    public long Epoch {get;}
    public FilterSpec Policy {get;}
    public ScanPriority Priority {get;}
    public Task<ScanProgress> Completion {get;}
    public RootChangeMonitor? Monitor {get;private set;}
    private readonly CancellationTokenSource stop;
    public bool Cancelled=>stop.IsCancellationRequested;
    public BackgroundScan(string rootId,string path,long epoch,FilterSpec policy,ScanPriority priority,
        SemaphoreSlim slots,CancellationToken application,Func<CancellationToken,Task<ScanProgress>> run,Func<RootChangeMonitor?>? createMonitor=null)
    {
        RootId=rootId;Path=path;Epoch=epoch;Policy=policy;Priority=priority;
        stop=CancellationTokenSource.CreateLinkedTokenSource(application);
        Completion=Task.Run(()=>WithSlot(slots,stop.Token,async token=>
        {
            Monitor=createMonitor?.Invoke();
            return await run(token).ConfigureAwait(false);
        }),CancellationToken.None);
    }
    public static async Task<ScanProgress> WithSlot(SemaphoreSlim slots,CancellationToken cancellation,Func<CancellationToken,Task<ScanProgress>> run)
    {
        await slots.WaitAsync(cancellation).ConfigureAwait(false);
        try{return await run(cancellation).ConfigureAwait(false);}
        finally{slots.Release();}
    }
    public void Cancel()=>stop.Cancel();
    public RootChangeMonitor? DetachMonitor()
    {
        if(!Completion.IsCompleted)throw new InvalidOperationException("扫描结束后才能移交监听器。");
        var monitor=Monitor;Monitor=null;return monitor;
    }
    public void Dispose()
    {
        if(!Completion.IsCompleted)throw new InvalidOperationException("扫描结束后才能释放其资源。");
        Monitor?.Dispose();stop.Dispose();
    }
}
