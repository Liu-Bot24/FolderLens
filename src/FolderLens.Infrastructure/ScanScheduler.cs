namespace FolderLens.Infrastructure;

/// <summary>Two bounded packet turns, never two whole-root reservations. One turn
/// prefers the displayed root; the other keeps older roots making progress.</summary>
public sealed class ScanScheduler
{
    private readonly object sync=new();
    private readonly List<Waiter> pending=[];
    private readonly HashSet<string> active=new(StringComparer.Ordinal);
    private string preferred="";
    public void Prefer(string root){lock(sync){preferred=root;Dispatch();}}
    public bool IsWaiting(string root){lock(sync)return pending.Any(w=>w.Root==root);}
    public (int Active,int Waiting) Counts {get{lock(sync)return(active.Count,pending.Count);}}
    public async Task<IDisposable> Enter(string root,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var waiter=new Waiter(root,token);
        lock(sync){pending.Add(waiter);Dispatch();}
        using var registration=token.Register(()=>{lock(sync){if(pending.Remove(waiter))waiter.Ready.TrySetCanceled(token);Dispatch();}});
        return await waiter.Ready.Task.ConfigureAwait(false);
    }
    private void Dispatch()
    {
        while(active.Count<2)
        {
            var eligible=pending.Where(w=>!active.Contains(w.Root)&&!w.Token.IsCancellationRequested);
            var next=eligible.FirstOrDefault(w=>w.Root==preferred)??eligible.FirstOrDefault();
            if(next is null)return;
            pending.Remove(next);active.Add(next.Root);next.Ready.SetResult(new Turn(this,next.Root));
        }
    }
    private void Release(string root){lock(sync){active.Remove(root);Dispatch();}}
    private sealed record Waiter(string Root,CancellationToken Token)
    {public TaskCompletionSource<IDisposable> Ready {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private sealed class Turn(ScanScheduler owner,string root):IDisposable
    {private ScanScheduler? scheduler=owner;public void Dispose()=>Interlocked.Exchange(ref scheduler,null)?.Release(root);}
}
