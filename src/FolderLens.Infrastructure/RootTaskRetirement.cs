namespace FolderLens.Infrastructure;

public static class RootTaskRetirement
{
    public static async Task<CancellationTokenSource> ResumeForNavigation(CancellationTokenSource stopped,Task? scan,Task? metadata,CancellationToken lifetime)
    {
        if(!stopped.IsCancellationRequested)return stopped;
        await Wait(scan,metadata,stopped.Token,lifetime).ConfigureAwait(false);
        return CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }
    public static async Task Wait(Task? scan,Task? metadata,CancellationToken retiredRoot,CancellationToken lifetime)
    {
        foreach(var task in new[]{scan,metadata})
        {
            if(task is null)continue;
            try{await task.ConfigureAwait(false);}
            catch(OperationCanceledException) when(retiredRoot.IsCancellationRequested&&!lifetime.IsCancellationRequested)
            {
                // Navigation intentionally cancelled this root. The task is now terminal;
                // its cancellation must not become a failure of the next root.
            }
        }
        lifetime.ThrowIfCancellationRequested();
    }
}
