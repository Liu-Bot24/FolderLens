using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Task<IReadOnlyList<IStorageItem>>? incomingDragItems;
    private readonly Dictionary<string,Task<ShellFileAction>> incomingDropDefaults=new(StringComparer.Ordinal);
    private nint FileOperationOwner=>WinRT.Interop.WindowNative.GetWindowHandle(this);
    private string? FileTransferDirectory=>!closing&&!replacingRoot&&!immersive&&activeCollectionId is null&&!string.IsNullOrWhiteSpace(rootId)?BrowsedDirectory:null;
    private Func<string,Task>? verifyTransferBarrier;
    private async Task<FileOperationTarget[]> CaptureTransferSelection()
    {
        var targets=new List<FileOperationTarget>();
        await VisitTransferSelection((target,_)=>{targets.Add(target);return Task.CompletedTask;});
        return targets.ToArray();
    }
    private async Task VisitTransferSelection(Func<FileOperationTarget,CancellationToken,Task> receive)
    {
        using var work=browserWork.Enter();if(work is null||catalog is not {} store)return;
        var view=ActiveBrowser;var source=view.ItemsSource;var ranges=SelectedOrdinals(view).ToArray();var handle=resultHandle;
        long version=rootChangeVersion;var listed=view.SelectedRanges.Select(r=>(r.FirstIndex,r.Length)).ToArray();
        bool Current()=>!closing&&version==rootChangeVersion&&ReferenceEquals(handle,resultHandle)&&ReferenceEquals(source,view.ItemsSource)&&listed.SequenceEqual(view.SelectedRanges.Select(r=>(r.FirstIndex,r.Length)));
        long count=ranges.Sum(r=>r.Count);var budget=new FileTransferBudget(count);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromSeconds(30));var token=deadline.Token;
        void Check(){token.ThrowIfCancellationRequested();if(!Current())throw new OperationCanceledException();}
        bool retained=false;long processed=0;
        async Task Accept(SnapshotItem item)
        {
            Check();var target=await store.ResolveFileOperation(item,token);Check();budget.Add(target.Path);
            await receive(target,token);if(verifyTransferBarrier is not null)await verifyTransferBarrier("item");Check();processed++;
        }
        try
        {
            Check();
            if(handle is not null)
            {
                retained=await store.RetainSnapshot(handle.Id,token);Check();if(!retained)throw new IOException("文件列表已变化，请重新选择。");
                foreach(var range in ranges)for(long offset=range.Start;offset<range.Start+range.Count;)
                {
                    Check();var page=await store.ReadPage(handle.Id,offset,(int)Math.Min(256,range.Start+range.Count-offset),token);
                    if(verifyTransferBarrier is not null)await verifyTransferBarrier("page");Check();
                    if(page.Count==0)throw new IOException("所选文件已变化，请刷新后重试。");
                    foreach(var item in page)await Accept(item);offset+=page.Count;
                    if(count>256)Status.Text=$"正在准备文件操作：{processed:N0} / {count:N0}；更改选择可取消。";
                }
            }
            else foreach(var range in ranges)for(long i=range.Start;i<range.Start+range.Count;i++)
            {Check();if(view.Items[(int)i] is FileRow {Item:{} item})await Accept(item);else throw new IOException("文件仍在加载，请稍后重试。");}
            Check();
        }
        catch(OperationCanceledException) when(deadline.IsCancellationRequested&&!lifetime.IsCancellationRequested)
        {throw new TimeoutException("准备文件操作超时，请减少选择后重试。");}
        finally{if(retained)await store.ReleaseSnapshot(handle!.Id);}
    }
    private async Task<IStorageItem[]> TransferStorageItems()
    {
        var files=new List<IStorageItem>();
        await VisitTransferSelection(async(target,token)=>files.Add(await StorageFile.GetFileFromPathAsync(target.Path).AsTask(token)));
        return files.ToArray();
    }
    private async Task SetFileClipboard(bool cut)
    {
        using var work=browserWork.Enter();if(work is null||fileOperationBusy)return;
        try
        {
            var files=await TransferStorageItems();if(files.Length==0||closing)return;
            var data=new DataPackage{RequestedOperation=cut?DataPackageOperation.Move:DataPackageOperation.Copy};data.SetStorageItems(files);
            Clipboard.SetContent(data);Clipboard.Flush();Status.Text=cut?$"已剪切 {files.Length:N0} 项，请到目标文件夹粘贴。":$"已复制 {files.Length:N0} 项，可到目标文件夹粘贴。";
        }
        catch(OperationCanceledException){}catch(Exception error){ShowError(error);}
    }
    private async void CutFiles(object sender,RoutedEventArgs args)=>await SetFileClipboard(true);
    private async void PasteFiles(object sender,RoutedEventArgs args)
    {
        if(FileTransferDirectory is not {} target)return;
        try{await ReceiveFiles(Clipboard.GetContent(),target,null);}catch(OperationCanceledException){}catch(Exception error){ShowError(error);}
    }
    private void InitializeFileTransfer()
    {
        Shell.DragLeave+=(_,_)=>ResetIncomingDrag();
        foreach(var view in new ListViewBase[]{FilesGrid,FilesList})
        {
            view.CanDragItems=true;view.CanReorderItems=false;
            view.DragStarting+=(_,args)=>args.AllowedOperations=DataPackageOperation.Copy|DataPackageOperation.Move;
            view.DragItemsStarting+=(_,args)=>
            {
                if(closing||fileOperationBusy){args.Cancel=true;return;}
                EndMarquee(false);var files=TransferStorageItems();
                args.Data.RequestedOperation=DataPackageOperation.Copy|DataPackageOperation.Move;
                // ListView/GridView expose DragItemsStarting without an event deferral.
                // Standard delayed rendering lets Windows request the captured selection asynchronously.
                args.Data.SetDataProvider(StandardDataFormats.StorageItems,async request=>
                {
                    var deferral=request.GetDeferral();
                    try{var items=await files;if(!closing&&items.Length>0)request.SetData(items);}
                    catch(OperationCanceledException){}catch(Exception error){if(!closing)ShowError(error);}finally{deferral.Complete();}
                });
                _=files.ContinueWith(t=>{var error=t.Exception;DispatcherQueue.TryEnqueue(()=>{if(!closing)ShowError(error!);});},TaskContinuationOptions.OnlyOnFaulted);
            };
            view.DragItemsCompleted+=async(_,_)=>await RefreshAfterShellOperation();
        }
        foreach(var binding in new[]{(VirtualKey.C,false),(VirtualKey.X,true)})
        {
            var accelerator=new Microsoft.UI.Xaml.Input.KeyboardAccelerator{Key=binding.Item1,Modifiers=VirtualKeyModifiers.Control};
            accelerator.Invoked+=async(_,args)=>{var focus=Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject;if(closing||ViewerEditorOwnsInput(focus)||ViewerPopupOwnsInput(focus)||(!IsInFileList(focus)&&!IsInViewerSurface(focus)))return;args.Handled=true;await SetFileClipboard(binding.Item2);};Shell.KeyboardAccelerators.Add(accelerator);
        }
        var paste=new Microsoft.UI.Xaml.Input.KeyboardAccelerator{Key=VirtualKey.V,Modifiers=VirtualKeyModifiers.Control};
        paste.Invoked+=(_,args)=>{var focus=Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject;if(FileTransferDirectory is null||ViewerEditorOwnsInput(focus)||ViewerPopupOwnsInput(focus))return;args.Handled=true;PasteFiles(this,new());};Shell.KeyboardAccelerators.Add(paste);
    }
    private string? DropDirectory(DependencyObject? original)
    {
        for(var node=original;node is not null;node=VisualTreeHelper.GetParent(node))
        {
            if(node is TreeViewItem item)
            {
                var folder=FolderTree.NodeFromContainer(item)?.Content as FolderNode;
                return folder?.PageOffset is null?folder?.Path:null;
            }
            if(ReferenceEquals(node,FolderTree))return null;
            if(ReferenceEquals(node,BrowserPane))return FileTransferDirectory;
        }
        return null;
    }
    private async void FilesDragOver(object sender,DragEventArgs args)
    {
        args.Handled=true;args.AcceptedOperation=DataPackageOperation.None;
        string? target=DropDirectory(args.OriginalSource as DependencyObject);
        if(target is null||fileOperationBusy||!args.DataView.Contains(StandardDataFormats.StorageItems))return;
        var deferral=args.GetDeferral();
        try
        {
            incomingDragItems??=args.DataView.GetStorageItemsAsync().AsTask();var files=await incomingDragItems;
            bool control=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control)!=0,shift=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift)!=0;
            if(control&&shift)return;
            if(!incomingDropDefaults.TryGetValue(target,out var normal))incomingDropDefaults[target]=normal=Task.Run(()=>ShellTransferPolicy.Choose(files.Select(f=>f.Path).ToArray(),target,false,false));
            var action=control?ShellFileAction.Copy:shift?ShellFileAction.Move:await normal;
            var effect=action==ShellFileAction.Move?DataPackageOperation.Move:DataPackageOperation.Copy;
            args.AcceptedOperation=(args.AllowedOperations&effect)!=0?effect:DataPackageOperation.None;
            args.DragUIOverride.Caption=(effect==DataPackageOperation.Move?"移动到 ":"复制到 ")+Path.GetFileName(target.TrimEnd('\\'));
        }
        catch(Exception error){args.AcceptedOperation=DataPackageOperation.None;RecordWebView($"Drop target unavailable {error.GetType().Name}");}finally{deferral.Complete();}
    }
    private async void FilesDrop(object sender,DragEventArgs args)
    {
        args.Handled=true;string? target=DropDirectory(args.OriginalSource as DependencyObject);if(target is null)return;
        var deferral=args.GetDeferral();
        try
        {
            var files=await args.DataView.GetStorageItemsAsync();
            bool control=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control)!=0,shift=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift)!=0;
            var action=await Task.Run(()=>ShellTransferPolicy.Choose(files.Select(f=>f.Path).ToArray(),target,control,shift));
            var effect=action==ShellFileAction.Move?DataPackageOperation.Move:DataPackageOperation.Copy;
            if((args.AllowedOperations&effect)==0)return;
            await ReceiveFiles(args.DataView,target,action);
        }
        catch(OperationCanceledException){}catch(Exception error){ShowError(error);}finally{ResetIncomingDrag();deferral.Complete();}
    }
    private void ResetIncomingDrag(){incomingDragItems=null;incomingDropDefaults.Clear();}
    private async Task ReceiveFiles(DataPackageView data,string destination,ShellFileAction? requested)
    {
        if(fileOperationBusy||closing||!data.Contains(StandardDataFormats.StorageItems))return;
        using var work=browserWork.Enter();if(work is null)return;
        fileOperationBusy=true;UpdateCommandAvailability();
        try
        {
            var files=await data.GetStorageItemsAsync();if(closing||files.Count==0)return;
            var action=requested??(data.RequestedOperation==DataPackageOperation.Move?ShellFileAction.Move:ShellFileAction.Copy);
            var requests=files.Select(f=>new ShellFileRequest(f.Path,action,destination)).ToArray();
            await ReturnToBrowser();if(closing)return;ClearResultSelection();
            var result=await ShellFileOperations.Execute(requests,FileOperationOwner,lifetime.Token);
            await RefreshAfterShellOperation();ShowShellResult(result);
            // A partial move must not tell the source to forget every cut item.
            if(!result.Aborted&&result.HResult>=0&&result.Items.All(i=>i.Outcome==ShellItemOutcome.Completed))
                data.ReportOperationCompleted(action==ShellFileAction.Move?DataPackageOperation.Move:DataPackageOperation.Copy);
        }
        finally{fileOperationBusy=false;UpdateCommandAvailability();}
    }
    private async Task RefreshAfterShellOperation()
    {
        using var work=browserWork.Enter();if(work is null)return;
        if(closing||catalog is null)return;
        try{if(activeCollectionId is {} collection){await catalog.RefreshPlaylist(collection,lifetime.Token);await RefreshQuery(preserveViewport:true);}else{reconcilePending=true;await Reconcile();}}
        catch(OperationCanceledException){}catch(Exception error){if(!closing)ShowError(error);}
    }
    private void ShowShellResult(ShellBatchResult result)
    {
        RecordWebView($"Shell operation items={result.Items.Count} completed={result.Items.Count(i=>i.Outcome==ShellItemOutcome.Completed)} aborted={result.Aborted} hr={result.HResult:X8}");
        if(closing)return;
        Status.Text=result.Items.Any(i=>i.Outcome==ShellItemOutcome.SourceRetained)?"目标已生成，但原文件仍在，移动未完成。":result.Items.Any(i=>i.Outcome==ShellItemOutcome.Indeterminate)?"无法确认部分文件的操作结果，请检查源位置和目标位置。":result.Aborted?"操作已取消；已完成的文件操作已保留。":result.Items.Any(i=>i.Outcome!=ShellItemOutcome.Completed)||result.HResult<0?"部分文件未完成操作，请检查系统提示及目标文件夹。":$"已完成 {result.Items.Count:N0} 项文件操作。";
    }
}
