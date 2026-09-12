namespace FolderLens.Core;

/// <summary>Stops producers before waiting for every entered operation, including its finally.</summary>
public sealed class WorkRetirement
{
    private readonly object gate=new();
    private readonly TaskCompletionSource drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopped;
    private int active;
    public IDisposable? Enter()
    {
        lock(gate){if(stopped)return null;active++;return new Registration(this);}
    }
    public Task Stop()
    {
        lock(gate){stopped=true;if(active==0)drained.TrySetResult();return drained.Task;}
    }
    private void Leave(){lock(gate){if(--active==0&&stopped)drained.TrySetResult();}}
    private sealed class Registration(WorkRetirement owner):IDisposable
    {
        private WorkRetirement? owner=owner;
        public void Dispose()=>Interlocked.Exchange(ref owner,null)?.Leave();
    }
}
