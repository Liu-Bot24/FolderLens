using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool fileOperationBusy;
    private async void RenameFile(object sender,RoutedEventArgs e)=>await OperateFile(FileOperationKind.Rename);
    private async void MoveFile(object sender,RoutedEventArgs e)=>await OperateFile(FileOperationKind.Move);
    private async void DeleteFile(object sender,RoutedEventArgs e)=>await OperateFile(FileOperationKind.Recycle);
    private async Task OperateFile(FileOperationKind kind)
    {
        if(fileOperationBusy||selected?.Item is null||catalog is null||closing)return;
        using var operation=browserWork.Enter();if(operation is null)return;
        fileOperationBusy=true;UpdateCommandAvailability();
        var row=selected;var item=row.Item;var store=catalog;string source=SourcePath(row);long rootVersion=rootChangeVersion;
        bool filesystemCompleted=false;
        bool OwnsOperation()
        {
            bool owns=!closing&&rootVersion==rootChangeVersion&&selected?.Item is {} current&&SameCollectionObservation(current,item);
            if(!owns)RecordWebView($"FileOperation superseded closing={closing} root={rootVersion}/{rootChangeVersion} selected={selected?.Item?.EntryId} expected={item.EntryId}");
            return owns;
        }
        try
        {
            if(ActiveBrowser.SelectedItems.Count>1)
            {
                await ShowOwnedDialog(new ContentDialog{XamlRoot=Shell.XamlRoot,Title="请选择一个文件",Content="重命名、移动和删除目前每次操作一个文件。",CloseButtonText="关闭"});return;
            }
            var target=await store.ResolveFileOperation(item,lifetime.Token);
            source=target.Path;
            if(!OwnsOperation())return;
            string? destination=null;
            if(kind==FileOperationKind.Rename)
            {
                var name=new TextBox{Text=Path.GetFileName(source),MinWidth=360,Header="新文件名（包含扩展名）"};
                var error=new TextBlock{TextWrapping=TextWrapping.Wrap};var body=new StackPanel{Spacing=12};body.Children.Add(name);body.Children.Add(error);
                var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="重命名文件",Content=body,PrimaryButtonText="重命名",CloseButtonText="取消"};
                dialog.PrimaryButtonClick+=(_,args)=>{try{destination=FileOperations.RenameTarget(source,name.Text);}catch(Exception ex){error.Text=ex.Message;args.Cancel=true;}};
                var answer=await ShowOwnedDialog(dialog);if(answer!=ContentDialogResult.Primary){RecordWebView($"FileOperation dialog result={answer}");return;}
            }
            else if(kind==FileOperationKind.Move)
            {
                var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder=await picker.PickSingleFolderAsync();if(folder is null)return;destination=Path.Combine(folder.Path,Path.GetFileName(source));
            }
            if(!OwnsOperation())return;
            await store.ResolveFileOperation(item,lifetime.Token);
            if(!OwnsOperation())return;
            // Await viewer retirement, then validate ownership again before mutation.
            await ReturnToBrowser();
            if(!OwnsOperation())return;
            target=await store.ResolveFileOperation(item,lifetime.Token);
            if(!OwnsOperation())return;
            ClearResultSelection();CancelThumbnails();ClearPrefetchedImages();ClearImage();
            Status.Text=kind==FileOperationKind.Recycle?"正在删除文件…":"正在更新文件…";
            await FileOperations.Execute(new(target.Path,target.Stamp,kind,destination,target.PhysicalIdentity),lifetime.Token);
            filesystemCompleted=true;
            if(verifyFileMutationCompleted is not null)await verifyFileMutationCompleted();
            await store.CompleteFileOperation(target);
            if(closing)return;
            await RefreshCollectionsAfterScan();
            RecordWebView($"FileOperation completed kind={kind} entry={item.EntryId}");
            if(!closing&&rootVersion==rootChangeVersion)
            {
                if(activeCollectionId is not null)await RefreshQuery();
                else await OpenRoot(root,true,recordHistory:false,preserveDirectoryScope:true);
                Status.Text=kind==FileOperationKind.Recycle?"删除操作已完成。":kind==FileOperationKind.Rename?"文件已重命名。":"文件已移动。";
            }
        }
        catch(OperationCanceledException){if(!closing){Status.Text=filesystemCompleted?"文件操作已完成，收藏清理未完成，请重新打开应用重试。":"文件操作已取消。";await RefreshQuery();}}
        catch(Exception error){RecordWebView($"FileOperation failed kind={kind} entry={item.EntryId} error={error}");if(filesystemCompleted){if(!closing)Status.Text="文件操作已完成，但收藏清理失败；请重新打开应用完成清理。";}else ShowError(error);if(!closing)await RefreshQuery();}
        finally{fileOperationBusy=false;UpdateCommandAvailability();}
    }
}
