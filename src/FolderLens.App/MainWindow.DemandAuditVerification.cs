using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace FolderLens.App;
public sealed partial class MainWindow
{
    private Action<ContentDialog>? verifyOwnedDialogOpened;
    private Func<Task>? verifyFileMutationCompleted;
    private static void InvokeDialogPrimary(ContentDialog dialog)
    {
        FrameworkElement? Find(DependencyObject parent)
        {
            if(parent is FrameworkElement {Name:"PrimaryButton"} button)return button;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(Find(VisualTreeHelper.GetChild(parent,i)) is {} found)return found;
            return null;
        }
        dialog.ApplyTemplate();dialog.UpdateLayout();
        ((IInvokeProvider)new ButtonAutomationPeer((Button)(Find(dialog)??throw new InvalidOperationException("Primary button missing"))).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private async Task VerifyDemandAudit(string source,Dictionary<string,object> report)
    {
        void Stage(string value)=>File.AppendAllText(Path.Combine(dataDirectory,"audit-stages.log"),value+Environment.NewLine);
        Stage("open-root");await OpenRoot(source);if(scanTask is not null)await scanTask;await RefreshQuery();
        Stage("root-ready");var row=(FileRow)results![0]!;await results.EnsureLoaded(row,lifetime.Token);
        if(Environment.GetCommandLineArgs().Contains("--verify-file-operation-close"))
        {
            await WaitUntil(()=>!queryBusy&&!replacingRoot,TimeSpan.FromSeconds(5));
            row=(FileRow)results![0]!;await results.EnsureLoaded(row,lifetime.Token);
            if(!await SelectBrowserOrdinal(results,0,lifetime.Token))throw new InvalidOperationException("Setup selection was superseded");
            var item=row.Item!;var collection=await catalog!.CreateCollection("Generated close test");await catalog.ChangeCollectionItems([collection.Id],[item],true);
            string original=SourcePath(row),renamed=Path.Combine(Path.GetDirectoryName(original)!,"renamed.png");
            var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            verifyOwnedDialogOpened=dialog=>{Stage("operation-dialog-opened");((TextBox)((StackPanel)dialog.Content).Children[0]).Text="renamed.png";InvokeDialogPrimary(dialog);};
            verifyFileMutationCompleted=async()=>{completed.TrySetResult();await release.Task;};
            Stage($"before-mutation selected={selected?.Name} item={selected?.Item?.EntryId} busy={fileOperationBusy} closing={closing}");
            var pending=OperateFile(FileOperationKind.Rename);
            if(await Task.WhenAny(completed.Task,pending).WaitAsync(TimeSpan.FromSeconds(10))==pending)
                throw new InvalidOperationException("Mutation ended before success: "+Status.Text+" | "+string.Join(" | ",webviewEvents.TakeLast(6)));
            verifyClosingState=async()=>
            {
                // External and internal moves share saved-location validation on the next read.
                await catalog.RefreshPlaylist(collection.Id,CancellationToken.None);
                bool pass=pending.IsCompletedSuccessfully&&File.Exists(renamed)&&!File.Exists(original)&&!(await catalog.ReadCollectionFlags([item]))[0];
                await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-close.json"),JsonSerializer.Serialize(new{status=pass?"PASS":"FAIL",mutationCompleted=true,savedLocationRefreshAfterMove=pass}));
                if(!pass)Environment.ExitCode=1;
            };
            lifetime.Token.Register(()=>release.TrySetResult());report["status"]="CLOSE_PENDING";return;
        }
        // Real visible-row path after explicit scan cancellation.
        Stage("properties-stop");scanStop.Cancel();visible.Add(row);int reads=0;
        verifyVideoPropertiesRead=_=>{reads++;return Task.CompletedTask;};
        await LoadRowProperties(row,true);
        if(reads!=1)throw new InvalidOperationException("停止扫描阻止属性读取。");
        report["propertiesAfterStop"]=true;
        Stage("properties-recycle");CancelThumbnails();var container=new GridViewItem();BindVisibleContainer(FilesGrid,container,row);
        var entered=Enumerable.Range(0,4).Select(_=>new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var released=Enumerable.Range(0,4).Select(_=>new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var retiredRequests=new List<CancellationTokenSource>();reads=0;
        verifyVideoPropertiesRead=async _=>{int index=reads++;entered[index].TrySetResult();await released[index].Task;};
        var loading=LoadRowProperties(row);
        try
        {
            for(int index=0;index<4;index++)
            {
                await entered[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
                foreach(var retiredRequest in retiredRequests)
                {
                    bool disposed=false;try{_ = retiredRequest.Token;}catch(ObjectDisposedException){disposed=true;}
                    if(!disposed)throw new InvalidOperationException("后继属性请求运行时，旧 CTS 仍未释放。");
                }
                if(index==3)break;
                retiredRequests.Add(propertyCancellations[row]);
                BindVisibleContainer(FilesGrid,container,null);BindVisibleContainer(FilesGrid,container,row);
                await LoadRowProperties(row);if(reads!=index+1)throw new InvalidOperationException("旧请求退出前启动了并发属性生产者。");
                released[index].TrySetResult();
            }
        }
        finally{foreach(var releaseRequest in released)releaseRequest.TrySetResult();}
        await loading.WaitAsync(TimeSpan.FromSeconds(5));verifyVideoPropertiesRead=null;
        if(reads!=4||propertyRequests.Count!=0||propertyCancellations.Count!=0||propertyRetryPending.Count!=0)throw new InvalidOperationException("重现行的属性请求未接续或未退场。");
        report["recycledRowSingleSuccessor"]=true;
        // Actual dialog Save button, deliberately blocked settings destination.
        Stage("settings-dialog");Directory.CreateDirectory(Path.Combine(dataDirectory,"config","slideshow.json"));var before=slideshowPreferences;
        verifyOwnedDialogOpened=dialog=>{((NumberBox)((StackPanel)dialog.Content).Children[0]).Value=17;InvokeDialogPrimary(dialog);};
        await ConfigureSlideshowCore().WaitAsync(TimeSpan.FromSeconds(5));verifyOwnedDialogOpened=null;
        if(slideshowPreferences!=before||!Status.Text.Contains("未能保存"))throw new InvalidOperationException("幻灯片保存失败没有保留旧状态或提示。");
        report["settingFailureHandled"]=true;
        Stage("rename-dialog");await SelectBrowserOrdinal(results,0,lifetime.Token);
        var opened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyOwnedDialogOpened=_=>opened.TrySetResult();
        var rename=OperateFile(FileOperationKind.Rename);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        verifyClosingState=async()=>
        {
            bool pass=rename.IsCompletedSuccessfully;
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-close.json"),JsonSerializer.Serialize(new{status=pass?"PASS":"FAIL",realRenameDialogClosedByShutdown=pass}));if(!pass)Environment.ExitCode=1;
        };
        report["status"]="CLOSE_PENDING";
    }
}
