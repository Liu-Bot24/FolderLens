namespace FolderLens.Core;

public static class StoppedScanPublication
{
    // Only an explicit Stop uses this path. It publishes committed results after
    // retirement, without restarting enumeration or using the progress throttle.
    public static async Task Run(Task? scan,CancellationToken lifetime,Func<bool> isCurrent,Func<Task> publish)
    {
        if(scan is not null)
        {
            try{await scan.WaitAsync(lifetime);}
            catch(OperationCanceledException) when(!lifetime.IsCancellationRequested){}
        }
        lifetime.ThrowIfCancellationRequested();
        if(isCurrent())await publish();
    }
}
