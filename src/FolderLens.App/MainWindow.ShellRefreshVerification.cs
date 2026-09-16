using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Data;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyShellRefreshNavigation(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A"),second=Path.Combine(source,"B");
        File.Copy(Path.Combine(first,"image-00.png"),Path.Combine(second,"one.png"));
        File.Copy(Path.Combine(first,"image-00.png"),Path.Combine(second,"two.png"));
        await OpenRoot(first);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var collection=await catalog!.CreateCollection("refresh");
        await catalog.ChangeCollectionItems([collection.Id],await catalog.ReadPage(resultHandle!.Id,0,32),true);
        await OpenRoot("collection:"+collection.Id);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed=default;int files=0;
        await using var worker=new ScanWorkerClient(ScanWorkerClient.FindExecutable(ScanWorkerDirectory)??throw new FileNotFoundException());
        var probe=typeof(CatalogStore).GetProperty("PlaylistProbeOverride",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        probe.SetValue(catalog,(Func<string,CancellationToken,Task<ScanDirectoryPacket>>)(async(path,token)=>
        {
            if(path!=first&&++files==2){observed=token;entered.TrySetResult();await release.Task;}
            return await worker.Probe(path,token);
        }));
        Task refresh=RefreshAfterShellOperation();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await OpenRoot(second);if(metadataTask is not null)await metadataTask;await RefreshQuery();
            ActiveBrowser.SelectRange(new ItemIndexRange(0,2));
            long request=queryRequest;var accepted=resultHandle;
            release.TrySetResult();await refresh.WaitAsync(TimeSpan.FromSeconds(10));await Task.Delay(100);
            if(!observed.IsCancellationRequested||queryRequest!=request||!ReferenceEquals(resultHandle,accepted)||ActiveBrowser.SelectedRanges.Sum(r=>(long)r.Length)!=2)
                throw new InvalidOperationException("Old Shell reconciliation changed the new directory or escaped navigation cancellation.");
            report["navigationCancelsOldCollectionRefresh"]=true;
            report["newDirectoryMultiselectionAndQueryPreserved"]=true;
            probe.SetValue(catalog,null);await OpenRoot("collection:"+collection.Id);
            files=0;entered=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            probe.SetValue(catalog,(Func<string,CancellationToken,Task<ScanDirectoryPacket>>)(async(path,token)=>
            {
                if(path!=first&&++files==2){entered.TrySetResult();await release.Task.WaitAsync(token);}
                return await worker.Probe(path,token);
            }));
            fileOperationBusy=true;CompleteShellOperation(new([],false,0),CaptureShellRefreshOwner());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(fileOperationBusy||shellRefreshTask is null||shellRefreshTask.IsCompleted)throw new InvalidOperationException("Completed Shell operation stayed busy during tail validation.");
            await OpenRoot(second);release.TrySetResult();await shellRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
            report["operationReleasedBeforeTailValidation"]=true;report["status"]="PASS";
        }
        finally{release.TrySetResult();probe.SetValue(catalog,null);await refresh;}
    }
}
