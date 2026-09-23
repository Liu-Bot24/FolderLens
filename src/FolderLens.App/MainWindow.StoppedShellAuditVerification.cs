using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task<ShellBatchResult> ExecuteShellFixture(params ShellFileRequest[] requests)
    {
        var execute=typeof(ShellFileOperations).GetMethod("ExecuteCore",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var result=await (Task<ShellBatchResult>)execute.Invoke(null,new object[]{requests,(nint)0,lifetime.Token,true})!;
        if(result.Aborted||result.Items.Any(i=>i.Outcome!=ShellItemOutcome.Completed))throw new InvalidOperationException("Fixture Shell operation failed.");
        return result;
    }

    private Task<string?> FixtureEntryState(string name)=>catalog!.Read(c=>
    {
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT entry_state FROM Files WHERE root_id=$root AND name=$name";
        cmd.Parameters.AddWithValue("$root",rootId);cmd.Parameters.AddWithValue("$name",name);
        return cmd.ExecuteScalar() as string;
    });

    private async Task StopFixtureScan(string root)
    {
        await OpenRoot(root);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;
        CancelScan(this,new());monitor?.Dispose();monitor=null;await RefreshQuery();
        if(!scanStop.IsCancellationRequested)throw new InvalidOperationException("Fixture scan did not stop.");
    }

    private async Task VerifyStoppedShellDrag(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A"),second=Path.Combine(source,"B"),child=Path.Combine(first,"X");
        Directory.CreateDirectory(child);
        string outgoing=Path.Combine(child,"outgoing.png");File.Copy(Path.Combine(first,"image-00.png"),outgoing);
        await StopFixtureScan(first);
        if(await FixtureEntryState("outgoing.png")!="present")throw new InvalidOperationException("Recursive drag fixture was not indexed.");
        var stopped=scanStop;var scan=scanTask;var owner=CaptureShellRefreshOwner();
        await ExecuteShellFixture(new ShellFileRequest(outgoing,ShellFileAction.Move,second));
        await CompleteOutgoingDrag(owner,[outgoing]).WaitAsync(TimeSpan.FromSeconds(15));
        if(await FixtureEntryState("outgoing.png")!="missing")throw new InvalidOperationException("Moved child file remains present in the stopped catalog.");
        if(!ReferenceEquals(stopped,scanStop)||!ReferenceEquals(scan,scanTask))throw new InvalidOperationException("Stopped root scan restarted after drag.");
        report["draggedChildRemovedFromCatalog"]=true;report["status"]="PASS";
    }

    private async Task VerifyStoppedShellOverlap(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A"),second=Path.Combine(source,"B"),x=Path.Combine(first,"X"),y=Path.Combine(first,"Y");
        Directory.CreateDirectory(x);Directory.CreateDirectory(y);
        string left=Path.Combine(second,"left.png"),right=Path.Combine(second,"right.png");
        File.Copy(Path.Combine(first,"image-00.png"),left);File.Copy(Path.Combine(first,"image-00.png"),right);
        await StopFixtureScan(first);
        var stopped=scanStop;var originalScan=scanTask;var heldScan=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanTask=heldScan.Task;
        try
        {
            var owner=CaptureShellRefreshOwner();
            CompleteShellOperation(await ExecuteShellFixture(new ShellFileRequest(left,ShellFileAction.Copy,x)),owner);
            Task firstRefresh=shellRefreshTask!;
            CompleteShellOperation(await ExecuteShellFixture(new ShellFileRequest(right,ShellFileAction.Copy,y)),owner);
            Task secondRefresh=shellRefreshTask!;
            heldScan.TrySetResult();await Task.WhenAll(firstRefresh,secondRefresh).WaitAsync(TimeSpan.FromSeconds(20));
            if(await FixtureEntryState("left.png")!="present"||await FixtureEntryState("right.png")!="present")
                throw new InvalidOperationException("Overlapping completed operations lost an affected directory.");
            if(!ReferenceEquals(stopped,scanStop)||!ReferenceEquals(heldScan.Task,scanTask))throw new InvalidOperationException("Stopped root scan restarted during overlap.");
            report["overlappingDirectoriesBothReconciled"]=true;report["status"]="PASS";
        }
        finally{heldScan.TrySetResult();scanTask=originalScan;}
    }

    private async Task VerifyStoppedShellTimeout(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A"),second=Path.Combine(source,"B"),child=Path.Combine(first,"X");
        Directory.CreateDirectory(child);
        string rootFile=Path.Combine(second,"root-new.png"),childFile=Path.Combine(second,"child-new.png");
        File.Copy(Path.Combine(first,"image-00.png"),rootFile);File.Copy(Path.Combine(first,"image-00.png"),childFile);
        await StopFixtureScan(first);
        var stopped=scanStop;var scan=scanTask;long before=queryRequest;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyShellRefreshBudget=TimeSpan.FromSeconds(4);
        verifyShellRefreshBeforeDirectory=async(path,token)=>
        {
            if(path!=child)return;
            entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);
        };
        try
        {
            CompleteShellOperation(await ExecuteShellFixture(new ShellFileRequest(rootFile,ShellFileAction.Copy,first),new ShellFileRequest(childFile,ShellFileAction.Copy,child)),CaptureShellRefreshOwner());
            await shellRefreshTask!.WaitAsync(TimeSpan.FromSeconds(10));
            if(!entered.Task.IsCompleted||await FixtureEntryState("root-new.png")!="present")
                throw new InvalidOperationException("Timeout fixture did not reach a committed first directory.");
            if(queryRequest<=before||!Status.Text.Contains("目录核对未完成",StringComparison.Ordinal))
                throw new InvalidOperationException("Completed operation timeout hid partial data or missing verification feedback.");
            if(resultHandle is null||!(await catalog!.ReadPage(resultHandle.Id,0,32)).Any(item=>item.RelativePath=="root-new.png"))
                throw new InvalidOperationException("Committed first directory was not published to the visible result.");
            if(!pendingShellDirectoryVersions.ContainsKey(child))throw new InvalidOperationException("Timed-out directory demand was discarded.");
            if(!ReferenceEquals(stopped,scanStop)||!ReferenceEquals(scan,scanTask))throw new InvalidOperationException("Stopped root scan restarted after timeout.");
            verifyShellRefreshBeforeDirectory=null;verifyShellRefreshBudget=null;
            await RefreshAfterShellOperation(CaptureShellRefreshOwner()).WaitAsync(TimeSpan.FromSeconds(15));
            if(await FixtureEntryState("child-new.png")!="present"||pendingShellDirectoryOrder.Count!=0)
                throw new InvalidOperationException("A later completed operation did not finish the pending directory check.");
            if(resultHandle is null||!(await catalog!.ReadPage(resultHandle.Id,0,32)).Any(item=>item.RelativePath==Path.Combine("X","child-new.png")))
                throw new InvalidOperationException("Reconciled child directory was not published to the visible result.");
            await OpenRoot(second);
            if(pendingShellDirectoryOrder.Count!=0||stoppedShellOwner is not null)
                throw new InvalidOperationException("Navigation retained old directory verification work.");
            report["partialCommitPublishedAndTimeoutVisible"]=true;report["status"]="PASS";
        }
        finally{verifyShellRefreshBeforeDirectory=null;verifyShellRefreshBudget=null;}
    }
}
