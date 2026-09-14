using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly List<BackgroundScan> backgroundScans=[];
    private readonly SemaphoreSlim backgroundScanSlots=new(2,2);
    private BackgroundScan? activeBackgroundScan;
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
        backgroundScans.Clear();backgroundScanSlots.Dispose();
    }
}
