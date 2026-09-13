using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string? verifyScanWorkerExecutable;
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
        verifyScanWorkerExecutable=Path.Combine(source,"missing-worker.exe");
        try
        {
            await OpenRoot(Path.Combine(source,"B"));await RefreshQuery();
            if(browserScanError is null||replacingRoot||FilesGrid.Items.Count!=0||BrowserEmptyTitle.Text!="暂时无法显示浏览结果")
                throw new InvalidOperationException("Worker failure was hidden by a loading/empty/success state.");
        }
        finally{verifyScanWorkerExecutable=null;}
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        if(browserScanError is not null||resultHandle?.Count!=12)throw new InvalidOperationException("Browser did not recover after a failed root open.");
        report["scanFailureVisibleAndRecovery"]=true;
        report["phase"]="complete";report["status"]="PASS";
    }
}
