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
    private CancellationTokenSource? incomingDragStop;
    private CancellationTokenSource? incomingPreparationStop;
    private Func<bool>? incomingPreparationCurrent;
    private readonly Dictionary<string,Task<ShellFileAction>> incomingDropDefaults=new(StringComparer.Ordinal);
    private nint FileOperationOwner=>WinRT.Interop.WindowNative.GetWindowHandle(this);
    private string? FileTransferDirectory=>!closing&&!replacingRoot&&!immersive&&activeCollectionId is null&&!string.IsNullOrWhiteSpace(rootId)?BrowsedDirectory:null;
    private Func<string,Task>? verifyTransferBarrier;
    private CancellationTokenSource? shellRefreshStop;
    private CancellationTokenSource shellViewStop=new();
    private Task? shellRefreshTask;
    private Task? stoppedShellRefreshTask;
    private ShellRefreshOwner? stoppedShellOwner;
    private readonly Queue<string> pendingShellDirectoryOrder=new();
    private readonly Dictionary<string,long> pendingShellDirectoryVersions=new(StringComparer.Ordinal);
    private long shellDirectoryDemandVersion;
    private TimeSpan? verifyShellRefreshBudget;
    private Func<string,CancellationToken,Task>? verifyShellRefreshBeforeDirectory;
    private sealed record ShellRefreshOwner(long RootVersion,long ViewRevision,string RootId,string? Collection,CancellationToken Token);
    private ShellRefreshOwner CaptureShellRefreshOwner()=>new(rootChangeVersion,viewRestoreRevision,rootId,activeCollectionId,shellViewStop.Token);
    private void RetireShellRefreshView()
    {
        shellViewStop.Cancel();shellViewStop.Dispose();shellViewStop=new();
        pendingShellDirectoryOrder.Clear();pendingShellDirectoryVersions.Clear();
        stoppedShellOwner=null;stoppedShellRefreshTask=null;
    }
    private Func<bool> CaptureIncomingView()
    {
        var owner=CaptureShellRefreshOwner();var view=ActiveBrowser;long selectedVersion=selection;
        var ranges=view.SelectedRanges.Select(r=>(r.FirstIndex,r.Length)).ToArray();
        string filter=System.Text.Json.JsonSerializer.Serialize(CurrentFilter());
        bool wasImmersive=immersive,wasFullScreen=fullScreen;
        return ()=>!closing&&owner.RootVersion==rootChangeVersion&&owner.ViewRevision==viewRestoreRevision
            &&owner.RootId==rootId&&owner.Collection==activeCollectionId&&ReferenceEquals(view,ActiveBrowser)&&selectedVersion==selection
            &&wasImmersive==immersive&&wasFullScreen==fullScreen&&ranges.SequenceEqual(view.SelectedRanges.Select(r=>(r.FirstIndex,r.Length)))
            &&filter==System.Text.Json.JsonSerializer.Serialize(CurrentFilter());
    }
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
            ShellRefreshOwner? dragOwner=null;
            Task<IStorageItem[]>? dragFiles=null;
            view.CanDragItems=true;view.CanReorderItems=false;
            view.DragStarting+=(_,args)=>args.AllowedOperations=DataPackageOperation.Copy|DataPackageOperation.Move;
            view.DragItemsStarting+=(_,args)=>
            {
                if(closing||fileOperationBusy){args.Cancel=true;return;}
                dragOwner=CaptureShellRefreshOwner();
                EndMarquee(false);var files=dragFiles=TransferStorageItems();
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
            view.DragItemsCompleted+=async(_,_)=>
            {
                var owner=dragOwner;var files=dragFiles;dragOwner=null;dragFiles=null;
                if(owner is null||files is null||owner.Token.IsCancellationRequested)return;
                try{await CompleteOutgoingDrag(owner,(await files).Select(item=>item.Path).ToArray());}
                catch(OperationCanceledException){}catch(Exception error){if(!closing)ShowError(error);}
            };
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
            var current=CaptureIncomingView();
            if(incomingDragStop is {IsCancellationRequested:true})ResetIncomingDrag();
            if(incomingDragStop is null){incomingDragStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);incomingDragStop.CancelAfter(TimeSpan.FromSeconds(30));}
            var stop=incomingDragStop;var token=stop.Token;
            incomingDragItems??=args.DataView.GetStorageItemsAsync().AsTask(token);var files=await incomingDragItems.WaitAsync(token);
            if(!current()||!ReferenceEquals(stop,incomingDragStop))return;
            ValidateIncomingFiles(files,target);
            bool control=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control)!=0,shift=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift)!=0;
            if(control&&shift)return;
            ShellFileAction action;
            if(control)action=ShellFileAction.Copy;
            else if(shift)action=ShellFileAction.Move;
            else
            {
                if(!incomingDropDefaults.TryGetValue(target,out var normal))incomingDropDefaults[target]=normal=ShellTransferPolicy.ChooseAsync(files.Select(f=>f.Path).ToArray(),target,false,false,ScanWorkerClient.FindExecutable(ScanWorkerDirectory),token);
                action=await normal;
            }
            if(!current()||token.IsCancellationRequested||!ReferenceEquals(stop,incomingDragStop))return;
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
            bool control=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control)!=0,shift=(args.Modifiers&Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift)!=0;
            await ReceiveFiles(args.DataView,target,null,(control,shift,args.AllowedOperations));
        }
        catch(OperationCanceledException){}catch(Exception error){ShowError(error);}finally{ResetIncomingDrag();deferral.Complete();}
    }
    private void ResetIncomingDrag(){var stop=incomingDragStop;incomingDragStop=null;stop?.Cancel();stop?.Dispose();incomingDragItems=null;incomingDropDefaults.Clear();}
    private void CancelIncomingPreparation(){incomingPreparationStop?.Cancel();ResetIncomingDrag();}
    private void CancelStaleIncomingPreparation()
    {
        if(incomingPreparationCurrent is not {} current)return;
        // An invalid draft is also different from the accepted transfer view.
        bool valid;try{valid=current();}catch(Exception error) when(error is ArgumentException or OverflowException or FormatException){valid=false;}
        if(!valid)CancelIncomingPreparation();
    }
    private static void ValidateIncomingFiles(IReadOnlyList<IStorageItem> files,string destination)
    {
        var budget=new FileTransferBudget(files.Count);
        foreach(var file in files)budget.Add(file.Path,destination);
    }
    private async Task ReceiveFiles(DataPackageView data,string destination,ShellFileAction? requested,(bool Control,bool Shift,DataPackageOperation Allowed)? drop=null)
    {
        if(fileOperationBusy||closing||!data.Contains(StandardDataFormats.StorageItems))return;
        using var work=browserWork.Enter();if(work is null)return;
        var refreshOwner=CaptureShellRefreshOwner();
        var current=CaptureIncomingView();
        using var preparation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        preparation.CancelAfter(TimeSpan.FromSeconds(30));var token=preparation.Token;
        void Check(){token.ThrowIfCancellationRequested();if(!current())throw new OperationCanceledException();}
        incomingPreparationStop=preparation;incomingPreparationCurrent=current;
        fileOperationBusy=true;UpdateCommandAvailability();
        try
        {
            var files=await data.GetStorageItemsAsync().AsTask(token).WaitAsync(token);Check();if(files.Count==0)return;
            ValidateIncomingFiles(files,destination);
            var action=requested??(data.RequestedOperation==DataPackageOperation.Move?ShellFileAction.Move:ShellFileAction.Copy);
            if(drop is {} dragging)
            {
                action=await ShellTransferPolicy.ChooseAsync(files.Select(f=>f.Path).ToArray(),destination,dragging.Control,dragging.Shift,ScanWorkerClient.FindExecutable(ScanWorkerDirectory),token);
                Check();var effect=action==ShellFileAction.Move?DataPackageOperation.Move:DataPackageOperation.Copy;
                if((dragging.Allowed&effect)==0)return;
            }
            var requests=files.Select(f=>new ShellFileRequest(f.Path,action,destination)).ToArray();
            // Preparation owns the original browser state. Once committed,
            // Shell keeps its captured destination and reports partial results.
            Check();incomingPreparationStop=null;incomingPreparationCurrent=null;ClearResultSelection();
            if(verifyTransferBarrier is not null)await verifyTransferBarrier("incoming-commit");
            var result=await ShellFileOperations.Execute(requests,FileOperationOwner,lifetime.Token);
            CompleteShellOperation(result,refreshOwner);
            // A partial move must not tell the source to forget every cut item.
            if(!result.Aborted&&result.HResult>=0&&result.Items.All(i=>i.Outcome==ShellItemOutcome.Completed))
                data.ReportOperationCompleted(action==ShellFileAction.Move?DataPackageOperation.Move:DataPackageOperation.Copy);
        }
        finally
        {
            if(ReferenceEquals(incomingPreparationStop,preparation)){incomingPreparationStop=null;incomingPreparationCurrent=null;}
            fileOperationBusy=false;UpdateCommandAvailability();
        }
    }
    private void CompleteShellOperation(ShellBatchResult result,ShellRefreshOwner owner)
    {
        ShowShellResult(result);
        // The Shell operation has ended. Validation is separate cancellable
        // work and must not keep file commands disabled for an entire playlist.
        fileOperationBusy=false;UpdateCommandAvailability();
        shellRefreshTask=RefreshAfterShellOperation(owner,result);
    }
    private Task CompleteOutgoingDrag(ShellRefreshOwner owner,IReadOnlyList<string> sourcePaths)
        =>RefreshAfterShellOperation(owner,null,sourcePaths);
    private Task RefreshAfterShellOperation(ShellRefreshOwner? owner=null,ShellBatchResult? result=null,IReadOnlyList<string>? outgoingSources=null)
    {
        if(closing||catalog is null)return Task.CompletedTask;
        owner??=CaptureShellRefreshOwner();
        if(owner.Token.IsCancellationRequested||owner.RootVersion!=rootChangeVersion||owner.ViewRevision!=viewRestoreRevision||owner.RootId!=rootId||owner.Collection!=activeCollectionId)
            return Task.CompletedTask;
        if(owner.Collection is null&&scanStop.IsCancellationRequested)return QueueStoppedShellRefresh(owner,result,outgoingSources);
        return RefreshActiveShellOperation(owner);
    }
    private void AddPendingShellDirectory(string path)
    {
        if(DirectoryBrowseScope.Relative(root,path) is null)return;
        if(!pendingShellDirectoryVersions.ContainsKey(path))pendingShellDirectoryOrder.Enqueue(path);
        pendingShellDirectoryVersions[path]=checked(++shellDirectoryDemandVersion);
    }
    private Task QueueStoppedShellRefresh(ShellRefreshOwner owner,ShellBatchResult? result,IReadOnlyList<string>? outgoingSources)
    {
        if(stoppedShellOwner!=owner)
        {
            pendingShellDirectoryOrder.Clear();pendingShellDirectoryVersions.Clear();
            stoppedShellOwner=owner;stoppedShellRefreshTask=null;
        }
        AddPendingShellDirectory(BrowsedDirectory);
        if(result is not null)foreach(var item in result.Items)
        {
            if(Path.GetDirectoryName(item.Request.Source) is {} sourceDirectory)AddPendingShellDirectory(sourceDirectory);
            if(item.ActualDestination is {} actual&&Path.GetDirectoryName(actual) is {} actualDirectory)AddPendingShellDirectory(actualDirectory);
            if(item.Request.Destination is {} destination)AddPendingShellDirectory(destination);
        }
        if(outgoingSources is not null)foreach(var source in outgoingSources)
            if(Path.GetDirectoryName(source) is {} directory)AddPendingShellDirectory(directory);
        if(stoppedShellRefreshTask is {IsCompleted:false})return stoppedShellRefreshTask;
        stoppedShellRefreshTask=RunStoppedShellRefresh(owner);
        return stoppedShellRefreshTask;
    }
    private void CompletePendingShellDirectory(string path,long version)
    {
        if(pendingShellDirectoryOrder.Dequeue()!=path)throw new InvalidOperationException("目录核对队列顺序已变化。");
        if(pendingShellDirectoryVersions[path]==version)pendingShellDirectoryVersions.Remove(path);
        else pendingShellDirectoryOrder.Enqueue(path);
    }
    private async Task RunStoppedShellRefresh(ShellRefreshOwner owner)
    {
        using var work=browserWork.Enter();if(work is null||catalog is null)return;
        using var refreshCancellation=CancellationTokenSource.CreateLinkedTokenSource(owner.Token,lifetime.Token);
        var previous=shellRefreshStop;shellRefreshStop=refreshCancellation;previous?.Cancel();
        var token=refreshCancellation.Token;
        bool Current()=>!closing&&!token.IsCancellationRequested&&stoppedShellOwner==owner
            &&owner.RootVersion==rootChangeVersion&&owner.ViewRevision==viewRestoreRevision&&owner.RootId==rootId&&activeCollectionId is null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(verifyShellRefreshBudget??TimeSpan.FromSeconds(30));
        try
        {
            if(scanTask is {} retiring)try{await retiring.WaitAsync(deadline.Token);}catch(OperationCanceledException) when(!deadline.IsCancellationRequested){}
            deadline.Token.ThrowIfCancellationRequested();if(!Current())return;
            string currentRoot=root;long currentEpoch=epoch;var exclusions=ScanExclusions();
            while(Current())
            {
                while(pendingShellDirectoryOrder.Count>0)
                {
                    deadline.Token.ThrowIfCancellationRequested();if(!Current())return;
                    string path=pendingShellDirectoryOrder.Peek();long version=pendingShellDirectoryVersions[path];
                    string? relative=DirectoryBrowseScope.Relative(currentRoot,path);
                    if(relative is null){CompletePendingShellDirectory(path,version);continue;}
                    bool exists=await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT 1 FROM Directories WHERE root_id=$root AND canonical_key=$path";cmd.Parameters.AddWithValue("$root",owner.RootId);cmd.Parameters.AddWithValue("$path",relative);return cmd.ExecuteScalar() is not null;},deadline.Token);
                    if(!exists){CompletePendingShellDirectory(path,version);continue;}
                    if(verifyShellRefreshBeforeDirectory is {} beforeDirectory)await beforeDirectory(path,deadline.Token);
                    var scanResult=await new DirectoryIndexer(catalog,ScanWorkerClient.FindExecutable(ScanWorkerDirectory)).Scan(owner.RootId,currentRoot,currentEpoch,true,exclusions,null,deadline.Token,scopeRelative:relative,descendants:false,preserveRootState:true);
                    deadline.Token.ThrowIfCancellationRequested();if(!Current())return;
                    if(scanResult.State!="ready")throw new IOException("文件操作已结束，但目录核对未完成，请刷新重试。");
                    CompletePendingShellDirectory(path,version);
                }
                await RefreshQuery(preserveViewport:true,scanPreview:true);
                if(!Current()||pendingShellDirectoryOrder.Count==0)return;
            }
        }
        catch(OperationCanceledException) when(deadline.IsCancellationRequested&&!token.IsCancellationRequested)
        {
            if(Current())
            {
                try{await RefreshQuery(preserveViewport:true,scanPreview:true);}
                catch(Exception error){if(Current())RecordWebView("Shell refresh publish failed "+error.GetType().Name);}
                if(Current())Status.Text="文件操作已完成，但目录核对未完成；请按 F5 刷新。";
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error)
        {
            if(Current())
            {
                try{await RefreshQuery(preserveViewport:true,scanPreview:true);}
                catch(Exception publishError){if(Current())RecordWebView("Shell refresh publish failed "+publishError.GetType().Name);}
                if(Current())ShowError(error);
            }
        }
        finally{if(ReferenceEquals(shellRefreshStop,refreshCancellation))shellRefreshStop=null;}
    }
    private async Task RefreshActiveShellOperation(ShellRefreshOwner owner)
    {
        using var work=browserWork.Enter();if(work is null)return;
        if(closing||catalog is null)return;
        bool OwnsView()=>!closing&&!owner.Token.IsCancellationRequested&&owner.RootVersion==rootChangeVersion&&owner.ViewRevision==viewRestoreRevision&&owner.RootId==rootId&&owner.Collection==activeCollectionId;
        if(!OwnsView())return;
        using var refreshCancellation=CancellationTokenSource.CreateLinkedTokenSource(owner.Token,lifetime.Token);
        var previous=shellRefreshStop;shellRefreshStop=refreshCancellation;previous?.Cancel();
        var token=refreshCancellation.Token;
        bool Current()=>!token.IsCancellationRequested&&OwnsView();
        try
        {
            if(!Current())return;
            if(owner.Collection is {} collection)
            {
                long nextPublication=0;
                await foreach(var _ in catalog.RefreshPlaylistBatches(collection,token))
                {
                    if(!Current())return;
                    if(System.Diagnostics.Stopwatch.GetTimestamp()<nextPublication)continue;
                    await RefreshQuery(preserveViewport:true,scanPreview:true);
                    if(!Current())return;
                    nextPublication=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency;
                }
                if(Current())await RefreshQuery(preserveViewport:true,scanPreview:true);
            }
            else{reconcilePending=true;await Reconcile();}
        }
        catch(OperationCanceledException){}catch(Exception error){if(Current())ShowError(error);}
        finally{if(ReferenceEquals(shellRefreshStop,refreshCancellation))shellRefreshStop=null;}
    }
    private void ShowShellResult(ShellBatchResult result)
    {
        RecordWebView($"Shell operation items={result.Items.Count} completed={result.Items.Count(i=>i.Outcome==ShellItemOutcome.Completed)} aborted={result.Aborted} hr={result.HResult:X8}");
        if(closing)return;
        Status.Text=result.Items.Any(i=>i.Outcome==ShellItemOutcome.SourceRetained)?"目标已生成，但原文件仍在，移动未完成。":result.Items.Any(i=>i.Outcome==ShellItemOutcome.Indeterminate)?"无法确认部分文件的操作结果，请检查源位置和目标位置。":result.Aborted?"操作已取消；已完成的文件操作已保留。":result.Items.Any(i=>i.Outcome!=ShellItemOutcome.Completed)||result.HResult<0?"部分文件未完成操作，请检查系统提示及目标文件夹。":$"已完成 {result.Items.Count:N0} 项文件操作。";
    }
}
