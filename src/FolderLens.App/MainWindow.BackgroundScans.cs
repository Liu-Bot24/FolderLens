using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly List<BackgroundScan> backgroundScans=[];
    private readonly ScanScheduler scanScheduler=new();
    private BackgroundScan? activeBackgroundScan;
    private async Task ObserveBackgroundCompletion(BackgroundScan scan)
    {
        using var operation=browserWork.Enter();if(operation is null)return;
        try{var result=await scan.Completion;scanLog?.Write("scan-completed",new{root=scan.RootId,result.Files,result.Directories,result.Errors,result.State});}
        catch(OperationCanceledException){scanLog?.Write("scan-cancelled",new{root=scan.RootId});}
        catch(Exception error){scanLog?.Write("scan-failed",new{root=scan.RootId,type=error.GetType().Name,error.HResult});RecordWebView("BackgroundScanError "+error.GetType().Name);}
        if(closing)return;
        bool acquired=false;
        try
        {
            // Serialize ownership transfer with root selection, including a return while the task completes.
            await rootChangeGate.WaitAsync(lifetime.Token);acquired=true;
            if(closing)return;
            if(!backgroundScans.Remove(scan))return;
            if(ReferenceEquals(activeBackgroundScan,scan)&&rootId==scan.RootId&&epoch==scan.Epoch)
            {
                monitor?.Dispose();monitor=scan.DetachMonitor();activeBackgroundScan=null;
            }
            scan.Dispose();
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        finally{if(acquired)rootChangeGate.Release();}
    }
    private void PreferScanDirectory(string expectedRoot,string path)
    {
        foreach(var scan in backgroundScans)
            if(scan.RootId==expectedRoot&&!scan.Completion.IsCompleted&&!scan.Cancelled)scan.Priority.Prefer(path);
    }
    private async Task RetireBackgroundScans()
    {
        foreach(var scan in backgroundScans.ToArray())
        {
            try{await scan.Completion;}catch(OperationCanceledException){}catch(Exception error){RecordWebView("BackgroundScanError "+error.GetType().Name);}
            finally{scan.Dispose();}
        }
        backgroundScans.Clear();
    }
}
