using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool fileOperationBusy;
    private FileRow? CurrentFileOperationSelection()=>ActiveBrowser.SelectedRanges.Sum(range=>(long)range.Length)==1?SelectionPreview(ActiveBrowser):null;
    private async void RenameFile(object sender,RoutedEventArgs e)=>await OperateFile(FileOperationKind.Rename);
    private async void MoveFile(object sender,RoutedEventArgs e)=>await OperateFile(FileOperationKind.Move);
    private async void DeleteFile(object sender,RoutedEventArgs e)=>await OperateFile(FileOperationKind.Recycle);
    private async Task OperateFile(FileOperationKind kind)
    {
        if(fileOperationBusy||catalog is null||closing)return;
        using var operation=browserWork.Enter();if(operation is null)return;
        fileOperationBusy=true;UpdateCommandAvailability();
        try
        {
            var targets=await CaptureTransferSelection();if(targets.Length==0)return;
            if(kind==FileOperationKind.Rename&&targets.Length!=1){await ShowOwnedDialog(new ContentDialog{XamlRoot=Shell.XamlRoot,Title="请选择一个文件",Content="重命名每次操作一个文件。",CloseButtonText="关闭"});return;}
            long version=rootChangeVersion;var view=ActiveBrowser;var source=view.ItemsSource;var handle=resultHandle;var selectedRanges=view.SelectedRanges.Select(r=>(r.FirstIndex,r.Length)).ToArray();
            bool Current()
            {
                bool sameHandle=ReferenceEquals(handle,resultHandle),sameSource=ReferenceEquals(source,view.ItemsSource),sameSelection=selectedRanges.SequenceEqual(view.SelectedRanges.Select(r=>(r.FirstIndex,r.Length)));
                bool current=!closing&&version==rootChangeVersion&&sameSource&&sameSelection;
                if(!current)RecordWebView($"File operation superseded closing={closing} root={version==rootChangeVersion} handle={sameHandle} source={sameSource} selection={sameSelection}");
                return current;
            }
            string? newName=null,destination=null;
            if(kind==FileOperationKind.Rename)
            {
                var name=new TextBox{Text=Path.GetFileName(targets[0].Path),MinWidth=360,Header="新文件名（包含扩展名）"};
                var error=new TextBlock{TextWrapping=TextWrapping.Wrap};var body=new StackPanel{Spacing=12};body.Children.Add(name);body.Children.Add(error);
                var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="重命名文件",Content=body,PrimaryButtonText="重命名",CloseButtonText="取消"};
                dialog.PrimaryButtonClick+=(_,args)=>{try{FileOperations.RenameTarget(targets[0].Path,name.Text);newName=name.Text;}catch(Exception ex){error.Text=ex.Message;args.Cancel=true;}};
                if(await ShowOwnedDialog(dialog)!=ContentDialogResult.Primary)return;
            }
            else if(kind==FileOperationKind.Move)
            {
                var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,FileOperationOwner);
                var pending=picker.PickSingleFolderAsync();
                var folder=await pending.AsTask(lifetime.Token);if(folder is null)return;destination=folder.Path;
            }
            if(!Current())return;
            await ReturnToBrowser();if(!Current())return;
            // A background publication can replace a snapshot without changing the
            // selection. Re-resolve the actual selected files, not the handle object.
            var currentTargets=await CaptureTransferSelection();
            if(!Current()||!targets.Select(t=>(t.Item.EntryId,t.Item.Version,t.Path,t.PhysicalIdentity,t.Stamp)).SequenceEqual(currentTargets.Select(t=>(t.Item.EntryId,t.Item.Version,t.Path,t.PhysicalIdentity,t.Stamp))))
                throw new IOException("所选文件已变化，请重新选择后操作。");
            ClearResultSelection();CancelThumbnails();ClearPrefetchedImages();ClearImage();
            var action=kind==FileOperationKind.Rename?ShellFileAction.Rename:kind==FileOperationKind.Move?ShellFileAction.Move:ShellFileAction.Recycle;
            var result=await ShellFileOperations.Execute(targets.Select(t=>new ShellFileRequest(t.Path,action,destination,newName,t.Stamp,t.PhysicalIdentity)).ToArray(),FileOperationOwner,lifetime.Token);
            if(verifyFileMutationCompleted is not null&&result.Items.All(i=>i.Outcome==ShellItemOutcome.Completed))await verifyFileMutationCompleted();
            // File operations do not mutate playlists. Filesystem observations and
            // the saved-location validator reconcile changes from any application.
            await RefreshAfterShellOperation();ShowShellResult(result);
        }
        catch(OperationCanceledException){if(!closing)Status.Text="操作已取消。";}
        catch(Exception error){RecordWebView($"FileOperation failed kind={kind} error={error}");if(!closing)ShowError(error);}
        finally{fileOperationBusy=false;UpdateCommandAvailability();}
    }
}
