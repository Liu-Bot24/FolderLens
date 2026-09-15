using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<CancellationToken,Task>? verifyRootAdmissionBarrier;
    private async Task VerifyProScanCloseout(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        monitor?.Dispose();monitor=null;
        await OpenRoot(Path.Combine(source,"A"));if(scanTask is not null)await scanTask;
        if(metadataTask is not null)await metadataTask;
        await RefreshQuery();
        string id=rootId;long observedEpoch=epoch;
        if(resultHandle?.Count!=12||RootPath.Text==root)throw new InvalidOperationException("Child scope fixture was not loaded.");
        ShowScanError(new IOException("Injected scan failure"));
        Search.Text="image-01";searchTimer?.Stop();await RefreshQuery();
        if(resultHandle?.Count!=1||browserScanError is null)throw new InvalidOperationException("Search stopped responding after a child scope scan error.");
        Search.Text="";searchTimer?.Stop();sortDescending=!sortDescending;await RefreshQuery();
        if(resultHandle?.Count!=12)throw new InvalidOperationException("Clearing search after a scan error did not restore the list.");
        report["childScopeErrorStillAllowsSearchAndSort"]=true;
        long request=queryRequest;browserRootReady=false;
        try{await RefreshQuery();if(queryRequest!=request)throw new InvalidOperationException("An unaccepted root allowed an old query.");}
        finally{browserRootReady=true;}
        report["failedRootDoesNotQueryPreviousIdentity"]=true;

        CancelScan(this,new RoutedEventArgs());
        byte[] png=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        await File.WriteAllBytesAsync(Path.Combine(source,"A","after-stop.png"),png);
        await OpenRoot(Path.Combine(source,"A"));if(scanTask is not null)await scanTask;
        await RefreshQuery();
        if(scanStop.IsCancellationRequested||rootId!=id||epoch!=observedEpoch||resultHandle?.Count!=13)
            throw new InvalidOperationException("Explicit child navigation after Stop failed to re-enumerate the scope.");
        report["stopThenNavigateRechecksSameEpoch"]=true;
        // Reject after the UI preflight, precisely where the old implementation
        // had already disposed its accepted list. Leave this last: budget latches.
        var retained=results;var handle=resultHandle;long oldEpoch=epoch;
        verifyRootAdmissionBarrier=_=>
        {
            typeof(CatalogStore).GetMethod("MarkBrowsingBudgetReached",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(catalog,null);
            return Task.CompletedTask;
        };
        try{await RefreshCurrentRoot();}
        finally{verifyRootAdmissionBarrier=null;}
        if(!ReferenceEquals(results,retained)||resultHandle!=handle||epoch!=oldEpoch||!browserRootReady||replacingRoot)
            throw new InvalidOperationException("Late admission refusal destroyed the accepted view.");
        Search.Text="after-stop";searchTimer?.Stop();await RefreshQuery();
        if(resultHandle?.Count!=1||epoch!=oldEpoch)throw new InvalidOperationException("A rejected refresh disabled filtering of committed files.");
        report["lateBudgetRefusalPreservesRowsAndFiltering"]=true;
        report["status"]="PASS";
    }
}
