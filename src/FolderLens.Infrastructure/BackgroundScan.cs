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
    public RootChangeMonitor? Monitor {get;set;}
    private readonly CancellationTokenSource stop;
    public bool Cancelled=>stop.IsCancellationRequested;
    public BackgroundScan(string rootId,string path,long epoch,FilterSpec policy,ScanPriority priority,
        SemaphoreSlim slots,CancellationToken application,Func<CancellationToken,Task<ScanProgress>> run)
    {
        RootId=rootId;Path=path;Epoch=epoch;Policy=policy;Priority=priority;
        stop=CancellationTokenSource.CreateLinkedTokenSource(application);
        Completion=Task.Run(async()=>
        {
            await slots.WaitAsync(stop.Token).ConfigureAwait(false);
            try{return await run(stop.Token).ConfigureAwait(false);}
            finally{slots.Release();}
        },CancellationToken.None);
    }
    public void Cancel()=>stop.Cancel();
    public void Dispose()
    {
        if(!Completion.IsCompleted)throw new InvalidOperationException("扫描结束后才能释放其资源。");
        Monitor?.Dispose();stop.Dispose();
    }
}
