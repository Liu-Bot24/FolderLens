using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string? verifyScanWorkerExecutable;
    private string? verifyScanWorkerDirectory;
    private Func<CancellationToken,Task>? verifyReconcileBarrier;
    private string ScanWorkerDirectory=>verifyScanWorkerDirectory??AppContext.BaseDirectory;
    private async Task VerifyScanAudit(string source,Dictionary<string,object> report)
    {
        string test=Environment.GetCommandLineArgs().Single(arg=>arg.StartsWith("--verify-scan-audit-"));
        report["case"]=test;report["phase"]="setup";
        if(test.EndsWith("r2"))
        {
            string executable=ScanWorkerClient.FindExecutable()??throw new FileNotFoundException("Missing scan worker.");
            verifyScanWorkerDirectory=Path.Combine(dataDirectory,"worker-layout");
            string legacy=Path.Combine(verifyScanWorkerDirectory,"FolderLens.Scan.Worker"),incomplete=Path.Combine(verifyScanWorkerDirectory,"scan-worker");
            Directory.CreateDirectory(legacy);Directory.CreateDirectory(incomplete);
            foreach(string file in Directory.GetFiles(Path.GetDirectoryName(executable)!))File.Copy(file,Path.Combine(legacy,Path.GetFileName(file)),true);
            File.Copy(executable,Path.Combine(incomplete,Path.GetFileName(executable)),true);
            report["phase"]="openWithIncompleteDedicatedWorker";
            try{await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();}
            finally{verifyScanWorkerDirectory=null;}
            if(browserScanError is not null||resultHandle?.Count!=12)throw new InvalidOperationException("OpenRoot bypassed the complete legacy worker: "+browserScanError);
            report["completeLegacyWorkerUsed"]=true;
        }
        else
        {
            string target=test.EndsWith("r4-empty")?Path.Combine(source,"B"):source;
            await OpenRoot(target);if(metadataTask is not null)await metadataTask;await RefreshQuery();
            const string failure="Injected scan component failure";
            ShowScanError(new ScanWorkerUnavailableException(failure));
            if(test.EndsWith("r3"))
            {
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                verifyCandidateBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
                var query=RefreshQuery();
                try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));ShowScanError(new ScanWorkerUnavailableException(failure));}
                finally{release.TrySetResult();verifyCandidateBarrier=null;}
                await query;await StartMetadataRefresh();
                Shell.UpdateLayout();
                bool visible=Status.Text.Contains(failure)||ResultSummary.Text.Contains(failure)||BrowserEmptyState.Visibility==Microsoft.UI.Xaml.Visibility.Visible&&BrowserEmptyDescription.Text.Contains(failure);
                if(Shell.FindName("BrowserScanErrorBar") is Microsoft.UI.Xaml.Controls.InfoBar bar)visible|=bar.IsOpen&&bar.Message.Contains(failure);
                report["items"]=FilesGrid.Items.Count;report["visibleError"]=visible;
                if(FilesGrid.Items.Count!=12||!visible)throw new InvalidOperationException("Scan error disappeared while partial results and metadata progress remained visible.");
            }
            else
            {
                report["phase"]="sameRootF5";string id=rootId;long revision=epoch;
                report["scanActiveBeforeF5"]=scanTask is {IsCompleted:false};
                if(test.EndsWith("r4-busy"))
                {
                    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    verifyReconcileBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
                    reconcilePending=true;var background=Reconcile();
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        report["scanActiveBeforeF5"]=scanTask is {IsCompleted:false};
                        var refresh=RefreshCurrentRoot();release.TrySetResult();
                        await Task.WhenAll(background,refresh).WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    finally{release.TrySetResult();verifyReconcileBarrier=null;}
                }
                else await RefreshCurrentRoot();
                if(metadataTask is not null)await metadataTask;
                report["scanErrorAfterF5"]=browserScanError??"";
                report["queryErrorAfterF5"]=browserEmptyError??"";
                report["scanActiveAfterF5"]=scanTask is {IsCompleted:false};
                if(rootId!=id||epoch!=revision)throw new InvalidOperationException("Test reopened the root instead of using F5 reconciliation.");
                if(!test.EndsWith("empty")){Search.Text="no-such-fixture-file";searchTimer?.Stop();await RefreshQuery();}
                Shell.UpdateLayout();
                if(browserScanError is not null||BrowserEmptyTitle.Text=="暂时无法显示浏览结果")throw new InvalidOperationException("Successful same-root F5 retained the old scan failure.");
                report["sameRootRecovery"]=true;
            }
        }
        report["phase"]="complete";report["status"]="PASS";
    }
    private async Task VerifyScanPipeline(string source,Dictionary<string,object> report)
    {
        string executable=ScanWorkerClient.FindExecutable()??throw new FileNotFoundException("Scan worker is missing.");
        report["worker"]=Path.GetRelativePath(AppContext.BaseDirectory,executable);
        report["phase"]="workerRead";
        await using(var client=new ScanWorkerClient(executable,TimeSpan.FromSeconds(5)))
        {
            int entries=0;bool completed=false;
            await foreach(var packet in client.Read(Path.Combine(source,"A"),false,lifetime.Token))
            {entries+=packet.Entries.Length;completed|=packet.State=="completed";}
            if(entries!=12||!completed)throw new InvalidOperationException($"Worker returned {entries} entries; completed={completed}.");
        }
        report["phase"]="rootIdentity";
        var opened=await new RootIdentityResolver(catalog!).Open(source,lifetime.Token);
        report["availability"]=opened.Availability;
        report["phase"]="catalogScan";
        var scan=await new DirectoryIndexer(catalog!,executable).Scan(opened.RootId,source,opened.Epoch,true,[],null,lifetime.Token);
        report["scan"]=scan;
        if(scan.State!="ready"||scan.Files!=12)throw new InvalidOperationException($"Scan ended {scan.State}, files={scan.Files}, errors={scan.Errors}.");
        report["phase"]="browserOpen";
        await OpenRoot(source);
        if(browserEmptyError is not null)throw new InvalidOperationException(browserEmptyError);
        if(metadataTask is not null)await metadataTask;
        await RefreshQuery();
        if(resultHandle?.Count!=12)throw new InvalidOperationException("Browser did not publish all 12 files.");
        report["phase"]="scanFailureFeedback";
        string acceptedRoot=rootId,rejectedRoot=Path.Combine(dataDirectory,"rejected-root");Directory.CreateDirectory(rejectedRoot);
        verifyScanWorkerExecutable=Path.Combine(source,"missing-worker.exe");
        try
        {
            await OpenRoot(rejectedRoot);await RefreshQuery();
            bool visible=Shell.FindName("BrowserScanErrorBar") is Microsoft.UI.Xaml.Controls.InfoBar bar&&bar.IsOpen;
            if(browserScanError is null||replacingRoot||rootId!=acceptedRoot||FilesGrid.Items.Count!=12||!visible)
                throw new InvalidOperationException("Rejected root admission lost the accepted results or hid the scan error.");
        }
        finally{verifyScanWorkerExecutable=null;}
        await RefreshCurrentRoot();if(metadataTask is not null)await metadataTask;await RefreshQuery();
        if(browserScanError is not null||resultHandle?.Count!=12)throw new InvalidOperationException("Browser did not recover after a failed root open.");
        report["scanFailureVisibleAndRecovery"]=true;
        report["phase"]="complete";report["status"]="PASS";
    }
}
