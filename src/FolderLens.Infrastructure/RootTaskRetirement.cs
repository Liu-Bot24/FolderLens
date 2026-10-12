namespace FolderLens.Infrastructure;

public static class RootTaskRetirement
{
    public static async Task<CancellationTokenSource> ResumeForNavigation(CancellationTokenSource stopped,Task? scan,Task? metadata,CancellationToken lifetime,Action<Exception>? observeRetiredFailure=null)
    {
        if(!stopped.IsCancellationRequested)return stopped;
        await Wait(scan,metadata,stopped.Token,lifetime,observeRetiredFailure).ConfigureAwait(false);
        return CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }
    public static async Task Wait(Task? scan,Task? metadata,CancellationToken retiredRoot,CancellationToken lifetime,Action<Exception>? observeRetiredFailure=null)
    {
        foreach(var task in new[]{scan,metadata})
        {
            lifetime.ThrowIfCancellationRequested();
            if(task is null)continue;
            try{await task.ConfigureAwait(false);}
            catch(OperationCanceledException) when(retiredRoot.IsCancellationRequested&&!lifetime.IsCancellationRequested)
            {
                // Navigation intentionally cancelled this root. The task is now terminal;
                // its cancellation must not become a failure of the next root.
            }
            catch(Exception error) when(retiredRoot.IsCancellationRequested&&!lifetime.IsCancellationRequested&&observeRetiredFailure is not null)
            {
                // Explicit navigation owns a new attempt. Observe a terminal old
                // failure and still retire the other producer; do not replay it
                // as failure of a root that has not even been probed yet.
                observeRetiredFailure(error);
            }
        }
        lifetime.ThrowIfCancellationRequested();
    }
}
