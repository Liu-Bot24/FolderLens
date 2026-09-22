namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyNavigationRetirement(string source,Dictionary<string,object> report)
    {
        var scanEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataCancelling=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? opening=null,metadata=null,navigation=null;
        try
        {
            verifyBackgroundScanCompletionBarrier=async token=>{scanEntered.TrySetResult();await scanRelease.Task.WaitAsync(token);};
            opening=OpenRoot(source);
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var oldScan=activeBackgroundScan??throw new InvalidOperationException("Expected an active old root scan.");
            verifyBackgroundScanCompletionBarrier=null;
            verifyVideoMetadataBarrier=async token=>
            {
                metadataEntered.TrySetResult();
                try{await Task.Delay(Timeout.Infinite,token);}
                catch(OperationCanceledException)
                {
                    // The decoder observed cancellation but is still retiring its
                    // resources. Independent scanning must stop before this ends.
                    metadataCancelling.TrySetResult();await metadataRelease.Task;throw;
                }
            };
            metadata=StartMetadataRefresh(explicitRequest:true);
            await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            verifyVideoMetadataBarrier=null;
            navigation=OpenRoot(Path.Combine(source,"B"));
            await metadataCancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            report["scanCancelledBeforeMetadataRetired"]=oldScan.Cancelled;
            report["metadataRetirementStillBlocked"]=!metadata.IsCompleted;
            bool pass=oldScan.Cancelled&&!metadata.IsCompleted;
            metadataRelease.TrySetResult();await navigation.WaitAsync(TimeSpan.FromSeconds(10));
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            report["newDirectoryAccepted"]=root==Path.Combine(source,"B");
            if(!pass)throw new InvalidOperationException("Navigation waits for metadata retirement before cancelling the old independent scan.");
            report["status"]="PASS";
        }
        finally
        {
            verifyBackgroundScanCompletionBarrier=null;verifyVideoMetadataBarrier=null;
            metadataRelease.TrySetResult();scanRelease.TrySetResult();
            foreach(var task in new[]{opening,metadata,navigation})if(task is not null)await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private async Task VerifyRetiredMetadataError(string source,Dictionary<string,object> report)
    {
        var observations=new List<object>();var failures=new List<string>();
        foreach(string transition in new[]{"category","root"})
        {
            await OpenRoot(source);if(metadataTask is not null)await metadataTask;
            monitor?.Dispose();monitor=null;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken retiredToken=default;Task? filling=null;
            try
            {
                verifyVideoMetadataBarrier=async token=>
                {
                    retiredToken=token;entered.TrySetResult();await release.Task;
                    throw new IOException("Injected late metadata provider failure");
                };
                filling=FillCurrentMetadata(explicitRequest:true);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                verifyVideoMetadataBarrier=null;
                if(transition=="root")await OpenRoot(Path.Combine(source,"B"));
                else
                {
                    long before=queryRequest;
                    SelectTag(Category,Tag(Category)=="media"?"text":"media");
                    await WaitUntil(()=>queryRequest>before&&!queryBusy,TimeSpan.FromSeconds(10));
                }
                const string acceptedStatus="verify-current-view-status";
                Status.Text=acceptedStatus;
                release.TrySetResult();await filling.WaitAsync(TimeSpan.FromSeconds(5));
                bool unchanged=Status.Text==acceptedStatus;
                observations.Add(new{transition,oldTokenCancelled=retiredToken.IsCancellationRequested,currentStatusPreserved=unchanged});
                if(!retiredToken.IsCancellationRequested||!unchanged)failures.Add(transition+" transition accepted an old metadata error.");
            }
            finally
            {
                verifyVideoMetadataBarrier=null;release.TrySetResult();
                if(filling is not null)await filling.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        report["retiredMetadataErrors"]=observations;
        Status.Text="verify-current-provider-before";
        try
        {
            verifyVideoMetadataBarrier=_=>throw new IOException("Injected current metadata provider failure");
            await FillCurrentMetadata(explicitRequest:true);
            bool surfaced=Status.Text.StartsWith("操作未完成：",StringComparison.Ordinal);
            report["currentMetadataFailureStillVisible"]=surfaced;
            if(!surfaced)failures.Add("Current metadata failure was incorrectly suppressed.");
        }
        finally{verifyVideoMetadataBarrier=null;}
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" ",failures));
        report["status"]="PASS";
    }
}
