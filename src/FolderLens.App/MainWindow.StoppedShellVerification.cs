using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyStoppedShellRefresh(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A"),second=Path.Combine(source,"B");
        await OpenRoot(first);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;
        CancelScan(this,new());monitor?.Dispose();monitor=null;await RefreshQuery();
        var stopped=scanStop;var scan=scanTask;
        string unrelated=Path.Combine(first,"unrelated");Directory.CreateDirectory(unrelated);File.Copy(Path.Combine(first,"image-00.png"),Path.Combine(unrelated,"not-requested.png"));
        string item=Path.Combine(second,"incoming.png");File.Copy(Path.Combine(first,"image-00.png"),item);
        var execute=typeof(ShellFileOperations).GetMethod("ExecuteCore",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        async Task Operate(ShellFileRequest request)
        {
            var owner=CaptureShellRefreshOwner();
            var result=await (Task<ShellBatchResult>)execute.Invoke(null,new object[]{new[]{request},(nint)0,lifetime.Token,true})!;
            if(result.Aborted||result.Items.Any(i=>i.Outcome!=ShellItemOutcome.Completed))throw new InvalidOperationException("Fixture Shell operation failed.");
            CompleteShellOperation(result,owner);await shellRefreshTask!.WaitAsync(TimeSpan.FromSeconds(15));
            if(!ReferenceEquals(stopped,scanStop)||!scanStop.IsCancellationRequested||!ReferenceEquals(scan,scanTask))throw new InvalidOperationException("Stopped root scan was restarted.");
        }
        await Operate(new(item,ShellFileAction.Copy,first));
        var copied=await catalog!.ReadFirstPage(CurrentFilter());
        if(!copied.Items.Any(i=>i.RelativePath=="incoming.png"))throw new InvalidOperationException("Completed copy was not published after stopping scan.");
        await Operate(new(Path.Combine(first,"incoming.png"),ShellFileAction.Move,second,"moved.png"));
        var moved=await catalog.ReadFirstPage(CurrentFilter());
        if(moved.Items.Any(i=>i.RelativePath=="incoming.png"))throw new InvalidOperationException("Completed move retained the old row after stopping scan.");
        if(moved.Items.Any(i=>i.RelativePath.Contains("not-requested",StringComparison.Ordinal)))throw new InvalidOperationException("Completed operation restarted an unrelated subtree scan.");
        report["copyAndMoveReflectedWhileRootRemainsStopped"]=true;report["status"]="PASS";
    }
}
