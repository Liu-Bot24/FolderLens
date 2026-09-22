using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyIncomingTransferNavigation(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A"),second=Path.Combine(source,"B");
        File.Copy(Path.Combine(first,"image-00.png"),Path.Combine(second,"one.png"));
        File.Copy(Path.Combine(first,"image-00.png"),Path.Combine(second,"two.png"));
        var file=await StorageFile.GetFileFromPathAsync(Path.Combine(first,"image-00.png"));
        foreach(string change in new[]{"navigation","selection","filter"})
        {
            bool navigate=change=="navigation";
            suppressFilters=true;try{Search.Text="";}finally{suppressFilters=false;}
            await OpenRoot(first);if(metadataTask is not null)await metadataTask;await RefreshQuery();
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool committed=false;
            var data=new DataPackage{RequestedOperation=DataPackageOperation.Copy};
            data.SetDataProvider(StandardDataFormats.StorageItems,async request=>
            {
                var deferral=request.GetDeferral();entered.TrySetResult();
                try{await release.Task;request.SetData(new IStorageItem[]{file});}
                finally{deferral.Complete();delivered.TrySetResult();}
            });
            // Stop at the boundary before any native file operation. The test
            // exercises the real delayed provider and actual WinUI selection.
            verifyTransferBarrier=stage=>
            {
                if(stage=="incoming-commit"){committed=true;throw new OperationCanceledException();}
                return Task.CompletedTask;
            };
            Task pending=ReceiveFiles(data.GetView(),first,null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if(navigate){await OpenRoot(second);if(metadataTask is not null)await metadataTask;await RefreshQuery();}
                if(change=="filter"){Search.Text="image-00";await RefreshQuery();}
                else ActiveBrowser.SelectRange(new ItemIndexRange(0,2));
                long selectedCount=ActiveBrowser.SelectedRanges.Sum(r=>(long)r.Length);
                var accepted=resultHandle;long request=queryRequest;
                try{await pending.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){}
                report[change+"RetiresProviderWaitBeforeDelivery"]=true;
                release.TrySetResult();
                try{await pending.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if(committed||!ReferenceEquals(accepted,resultHandle)||request!=queryRequest||ActiveBrowser.SelectedRanges.Sum(r=>(long)r.Length)!=selectedCount||fileOperationBusy)
                    throw new InvalidOperationException("Delayed transfer changed the current selection or reached the operation boundary.");
                report[change+"RejectsDelayedTransfer"]=true;
            }
            finally{release.TrySetResult();verifyTransferBarrier=null;try{await pending;}catch(OperationCanceledException){}}
        }
        foreach(bool stopped in new[]{false,true})
        {
            await OpenRoot(first,forceRefresh:true);if(metadataTask is not null)await metadataTask;await RefreshQuery();
            if(stopped)CancelScan(this,new());
            bool committed=false;
            var data=new DataPackage{RequestedOperation=DataPackageOperation.Copy};data.SetStorageItems(new IStorageItem[]{file});
            verifyTransferBarrier=stage=>
            {
                if(stage=="incoming-commit"){committed=true;throw new OperationCanceledException();}
                return Task.CompletedTask;
            };
            try{await ReceiveFiles(data.GetView(),first,null);}catch(OperationCanceledException){}
            finally{verifyTransferBarrier=null;}
            if(!committed||fileOperationBusy)throw new InvalidOperationException("An unchanged browser must accept transfer preparation even after stopping its scan.");
            report[stopped?"stoppedScanAcceptsTransfer":"unchangedViewAcceptsTransfer"]=true;
        }
        report["status"]="PASS";
    }
}
