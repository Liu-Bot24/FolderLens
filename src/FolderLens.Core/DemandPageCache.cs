namespace FolderLens.Core;

/// <summary>Loads pages only for active consumers. Evicting cached data never invalidates a consumer's row.</summary>
public sealed class DemandPageCache<T>(Func<int,CancellationToken,Task<IReadOnlyList<T>>> read,int capacity=16,int concurrency=2) : IDisposable
{
    private readonly object sync=new();
    private readonly Dictionary<int,Entry> entries=[];
    private readonly CancellationTokenSource stop=new();
    private readonly SemaphoreSlim slots=new(concurrency,concurrency);
    private long clock;
    private bool disposed;
    private sealed class Entry(CancellationToken parent)
    {
        public readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(parent);
        public Task<IReadOnlyList<T>> Task=null!;
        public int Readers;
        public long LastUse;
    }
    public async Task<IReadOnlyList<T>> Read(int page,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Entry entry;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!entries.TryGetValue(page,out entry!))
            {
                entry=new(stop.Token);entries.Add(page,entry);
                entry.Task=Load(page,entry.Stop.Token);
            }
            entry.Readers++;entry.LastUse=++clock;
        }
        try{return await entry.Task.WaitAsync(cancellation).ConfigureAwait(false);}
        finally
        {
            lock(sync)
            {
                entry.Readers--;
                if(entry.Readers==0&&(disposed||!entry.Task.IsCompletedSuccessfully))
                {
                    if(entries.TryGetValue(page,out var current)&&ReferenceEquals(current,entry))entries.Remove(page);
                    Retire(entry);
                }
                foreach(var victim in entries.Where(pair=>pair.Value.Readers==0).OrderBy(pair=>pair.Value.LastUse).Take(Math.Max(0,entries.Count-capacity)).ToArray())
                {entries.Remove(victim.Key);Retire(victim.Value);}
            }
        }
    }
    private async Task<IReadOnlyList<T>> Load(int page,CancellationToken cancellation)
    {
        await slots.WaitAsync(cancellation).ConfigureAwait(false);
        try{return await read(page,cancellation).ConfigureAwait(false);}
        finally{slots.Release();}
    }
    private static void Retire(Entry entry)
    {
        entry.Stop.Cancel();
        _=entry.Task.ContinueWith(task=>{if(task.IsFaulted)_=task.Exception;entry.Stop.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    public void Dispose()
    {
        lock(sync)
        {
            if(disposed)return;disposed=true;stop.Cancel();
            // Active readers retire their own entry after cancellation unwinds.
            foreach(var entry in entries.Values.Where(entry=>entry.Readers==0))Retire(entry);
            entries.Clear();stop.Dispose();
        }
    }
}
