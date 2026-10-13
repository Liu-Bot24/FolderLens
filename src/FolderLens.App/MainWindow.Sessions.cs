using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool restoringView;
    private long viewRestoreRevision;
    private sealed record PendingViewRestore(SavedView View,long Revision,long RootVersion,long InteractionVersion,long NavigationVersion);
    private PendingViewRestore? pendingViewRestore;
    private sealed record PendingPreviewRestore(PreviewBookmark Bookmark,string Path,long RootVersion);
    private PendingPreviewRestore? pendingPreviewRestore;
    private long previewReadySelection=-1;
    private PreviewBookmark? CapturePreviewBookmark()
    {
        if(previewReadySelection!=selection||selected is not {} row||Stamp(row) is not {} stamp||row.Kind is not ("image" or "text" or "markdown"))return null;
        return new(stamp.Length,stamp.ModifiedUtcTicks,row.Kind,imagePage,displayedText?.Start??0,textEncoding,TextScroll.VerticalOffset,MarkdownHost.Visibility==Visibility.Visible);
    }
    private PreviewBookmark? TakePreviewBookmark(FileRow row)
    {
        var pending=pendingPreviewRestore;pendingPreviewRestore=null;
        if(pending is null||pending.RootVersion!=rootChangeVersion||!string.Equals(pending.Path,BrowserPath(row),StringComparison.Ordinal)||Stamp(row) is not {} stamp)return null;
        return pending.Bookmark.ForFile(stamp.Length,stamp.ModifiedUtcTicks,row.Kind);
    }
    private string? VisibleBrowserPath()
    {
        var list=DetailsMode.IsChecked==true?(ListViewBase)FilesList:FilesGrid;
        int index=list.ItemsPanelRoot switch{ItemsWrapGrid panel=>panel.FirstVisibleIndex,ItemsStackPanel panel=>panel.FirstVisibleIndex,_=>-1};
        return index>=0&&index<list.Items.Count?BrowserPath(list.Items[index] as FileRow):null;
    }
    private SavedView CaptureView(FilterSpec? filter=null)=>new(root,filter??CurrentFilter(),BrowserPath(selected),immersive?browserOffset:FindScrollViewer(DetailsMode.IsChecked==true?FilesList:FilesGrid)?.VerticalOffset??0,DetailsMode.IsChecked==true,
        immersive?(selected?.Ordinal==browserEntryOrdinal?browserAnchorPath:BrowserPath(selected)):VisibleBrowserPath(),CapturePreviewBookmark());
    private SavedView? CaptureClosingView()=>lastAppliedFilter is {} filter&&filter.RootId==rootId?CaptureView(filter):null;
    private SavedView? CaptureAcceptedView()=>TrySubmittedBrowserFilter(out var filter)?CaptureView(filter):CaptureClosingView();
    private static FilterSpec NormalizeSavedBrowserFilter(FilterSpec filter)
    {
        filter=filter.ForBrowserView();string category=FileCategories.FromFilter(filter);
        return filter with{Kinds=FileCategories.Kinds(category),Extensions=FileCategories.Extensions(category),
            MaxFolderLevels=filter.CollectionId is null?filter.MaxFolderLevels:null,
            Sort=BrowserSortOptions.IsApplicable(category,filter.Sort.Field)?filter.Sort:filter.Sort with{Field="name"}};
    }
    private void ApplySavedFilter(FilterSpec filter)
    {
        filter.Validate();filter=filter.ForBrowserView();suppressFilters=true;searchTimer?.Stop();
        try
        {
            activeCollectionId=filter.CollectionId;includedCollectionIds=filter.IncludeCollections.ToArray();excludedCollectionIds=filter.ExcludeCollections.ToArray();UpdateCollectionFilterLabel();
            advanced=filter;folderGrouping=filter.Grouping;UpdateGroupingButton();Search.Text=filter.NamePathQuery;SetFormatChoices(filter);
            string category=FileCategories.FromFilter(filter);
            SelectTag(Category,category);UpdateSortOptions();ApplyDetailColumns();SelectTag(RawMode,filter.Raw);SelectTag(AnimationMode,filter.Animation);SelectTag(SortField,FolderLens.Core.BrowserSortOptions.IsApplicable(category,filter.Sort.Field)?filter.Sort.Field:"name");
            ShowHidden.IsChecked=filter.ShowHidden;PendingView.IsChecked=filter.IncludePending;browseDepth=filter.Recursive?filter.MaxFolderLevels:1;UpdateBrowseDepthLabel();sortDescending=filter.Sort.Direction=="desc";
            MinSize.Value=filter.Ranges.TryGetValue("logicalBytes",out var size)?size.Min/1048576.0??double.NaN:double.NaN;MaxSize.Value=size?.Max/1048576.0??double.NaN;
            SyncDimensionMinima(filter);
        }
        finally{suppressFilters=false;UpdateDetailSort();}
    }
    private Task RestoreSavedView(SavedView saved,bool recordHistory=true,bool recheckDirectory=false,Action? onAccepted=null,Action<SavedView>? onViewAccepted=null)
        =>saved.Filter.NeedsCollectionRuleBase?RestoreLegacyCollectionView(saved,recordHistory,recheckDirectory,onAccepted,onViewAccepted):RestoreSavedViewCore(saved,recordHistory,recheckDirectory,onAccepted,onViewAccepted);
    private async Task RestoreLegacyCollectionView(SavedView saved,bool recordHistory,bool recheckDirectory,Action? onAccepted,Action<SavedView>? onViewAccepted)
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        CancelPendingFileOpen();CancelPendingSearch();pendingViewRestore=null;pendingPreviewRestore=null;
        long revision=++viewRestoreRevision,navigation=++directoryNavigationRequest,version=rootChangeVersion;
        restoringView=true;
        try
        {
            var filter=await ConfirmCollectionRuleBase(saved.Filter);
            if(filter is null||closing||revision!=viewRestoreRevision||navigation!=directoryNavigationRequest)return;
            await RestoreSavedViewCore(saved with{Filter=filter},recordHistory,recheckDirectory,onAccepted,onViewAccepted);
        }
        finally
        {
            if(revision==viewRestoreRevision)
            {
                restoringView=false;
                if(!closing&&version==rootChangeVersion&&navigation==directoryNavigationRequest)
                {if(activeCollectionId is not null)_=StartMetadataRefresh();if(reconcilePending)_=Reconcile();}
            }
        }
    }
    private async Task<FilterSpec?> ConfirmCollectionRuleBase(FilterSpec filter)
    {
        var input=new TextBox{Header="原来作为相对路径起点的文件夹",PlaceholderText=@"例如 D:\照片",MinWidth=380};
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap};var panel=new StackPanel{Spacing=12};
        panel.Children.Add(new TextBlock{Text="这组旧收藏规则使用相对路径，但没有记录原根目录。请确认原来的起点；取消会保留原设置，不应用这组规则。",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(input);panel.Children.Add(error);
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="确认旧收藏规则的路径",Content=panel,PrimaryButtonText="确认并应用",CloseButtonText="取消"};
        FilterSpec? resolved=null;
        dialog.PrimaryButtonClick+=(_,args)=>
        {try{resolved=filter.WithCollectionRuleBase(input.Text.Trim());}catch(Exception failure){resolved=null;args.Cancel=true;error.Text=UserMessages.Error(failure);}};
        using var cancellation=lifetime.Token.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog.Hide()));
        verifyCollectionRuleMigration?.Invoke(dialog,input);
        return await dialog.ShowAsync()==ContentDialogResult.Primary?resolved:null;
    }
    private async Task RestoreSavedViewCore(SavedView saved,bool recordHistory=true,bool recheckDirectory=false,Action? onAccepted=null,Action<SavedView>? onViewAccepted=null)
    {
        CancelPendingFileOpen();
        saved.Filter.Validate();saved=saved with{Filter=NormalizeSavedBrowserFilter(saved.Filter)};
        // A same-root restore is a new navigation intent too. Retire older
        // preflight requests before they can admit another root or its controls.
        long navigation=++directoryNavigationRequest;
        // Old sessions and capacity navigation may describe a child scope of a
        // larger root. Restore the actual displayed directory as the scan root.
        if(saved.Filter.CollectionId is null&&saved.Filter.DirectoryScope.Length>0)
        {
            string previousRoot=saved.Root,nextRoot=Path.Combine(previousRoot,saved.Filter.DirectoryScope);
            string? Rebase(string? path)=>path is null?null:DirectoryBrowseScope.Relative(nextRoot,Path.Combine(previousRoot,path));
            saved=saved with{Root=nextRoot,Filter=saved.Filter with{DirectoryScope=""},SelectedPath=Rebase(saved.SelectedPath),ScrollAnchorPath=Rebase(saved.ScrollAnchorPath)};
        }
        var previous=CaptureAcceptedView();
        bool sameRoot=previous is not null&&browserRootReady&&!replacingRoot&&string.Equals(root,saved.Root,StringComparison.Ordinal)&&previous.Filter.HasSameScanPolicy(saved.Filter);
        restoringView=true;long revision=++viewRestoreRevision,requestedRoot=rootChangeVersion+(sameRoot?0:1);
        long interaction=browserInteractionVersion;bool accepted=false;
        bool OwnsRestore()=>!closing&&revision==viewRestoreRevision&&navigation==directoryNavigationRequest;
        RetireShellRefreshView();
        CancelIncomingPreparation();
        pendingViewRestore=null;pendingPreviewRestore=null;
        void AcceptView()
        {
            // OpenRoot owns a new navigation ID and already checked its
            // admission guard before invoking this synchronous callback.
            if(!sameRoot)navigation=directoryNavigationRequest;
            if(!OwnsRestore())return;
            accepted=true;requestedRoot=rootChangeVersion;
            if(interaction==browserInteractionVersion)
            {
                pendingViewRestore=new(saved,revision,requestedRoot,interaction,navigation);
                // Desktop/view restoration can have used the view-switch path.
                // Reset that prior state before any first page is published.
                if(saved.ScrollOffset<=0&&saved.ScrollAnchorPath is null&&saved.SelectedPath is null)ResetBrowserStart();
                else{keepBrowserStart=false;SetBrowserOffsetOwner(null);}
            }
            // This request's normalized filter is authoritative even before the
            // same-root query updates submittedBrowserFilter. Never sample later UI.
            onViewAccepted?.Invoke(saved with{Root=root,Filter=saved.Filter with{RootId=rootId,ObservedRootEpoch=activeCollectionId is null?epoch:null}});
            onAccepted?.Invoke();
        }
        try
        {
            if(sameRoot&&activeCollectionId is null&&(recheckDirectory||previous!.Filter.DirectoryScope!=saved.Filter.DirectoryScope))
            {
                if(scanStop.IsCancellationRequested&&!catalog!.BrowsingBudgetReached)
                {
                    // Stop remains stopped until explicit directory navigation.
                    // Retire its work before giving this scope a fresh token; the
                    // accepted root/epoch and the visible rows stay unchanged.
                    var stopped=scanStop;
                    long cancelRevision=scanCancelRevision;
                    var resumed=await RootTaskRetirement.ResumeForNavigation(stopped,scanTask,metadataTask,lifetime.Token,RecordRetiredRootFailure);
                    if(!OwnsRestore()||requestedRoot!=rootChangeVersion||cancelRevision!=scanCancelRevision||!ReferenceEquals(scanStop,stopped)){resumed.Dispose();return;}
                    scanStop=resumed;
                    stopped.Dispose();
                }
                if(verifySameRootRestoreBarrier is not null)await verifySameRootRestoreBarrier(lifetime.Token);
                if(!OwnsRestore()||requestedRoot!=rootChangeVersion)return;
                await new FolderLens.Infrastructure.ScanDirtyDirectories(catalog!).Mark(rootId,epoch,[new(saved.Filter.DirectoryScope,"BrowseNavigation",true)],lifetime.Token);
                if(!OwnsRestore()||requestedRoot!=rootChangeVersion)return;
                reconcilePending=true;PreferScanDirectory(rootId,saved.Filter.DirectoryScope);
            }
            if(sameRoot)
            {
                if(!OwnsRestore())return;
                ApplySavedFilter(saved.Filter);RootPath.Text=activeCollectionId is null?Path.Combine(saved.Root,saved.Filter.DirectoryScope):"收藏夹："+CollectionLabel(activeCollectionId);DetailsMode.IsChecked=saved.Details;UpdatePathPresentationControl();
                AcceptView();
                if(activeCollectionId is null)
                {
                    ShowTreeRoot(BrowsedDirectory);
                    if(activeTreeRoot is {} node)node.Content=new FolderNode(BrowsedDirectory,(node.Content as FolderNode)?.Label??FolderLabel(BrowsedDirectory),rootId,saved.Filter.DirectoryScope,root,Icon:(node.Content as FolderNode)?.Icon);
                    QueueTreeRefresh();
                }
                if(recordHistory&&previous is not null)navigationHistory.VisitFrom(previous);UpdateNavigationButtons();
                await RefreshQuery(preserveViewport:false);
            }
            else await OpenRoot(saved.Root,recordHistory:recordHistory,previousView:previous,preserveDirectoryScope:true,restoredView:saved,onAccepted:AcceptView,canAccept:()=>revision==viewRestoreRevision);
            if(!accepted||!OwnsRestore())return;
            if(revision==viewRestoreRevision&&activeCollectionId is null)RootPath.Text=BrowsedDirectory;
            await TryRestoreBrowserView();
        }
        finally
        {
            if(revision==viewRestoreRevision)
            {
                restoringView=false;
                // A fast metadata pass may have finished while restoration held
                // query publication. Drain once more after releasing that gate.
                if(activeCollectionId is not null&&requestedRoot==rootChangeVersion&&OwnsRestore())_=StartMetadataRefresh();
                if(reconcilePending&&requestedRoot==rootChangeVersion&&OwnsRestore())_=Reconcile();
            }
        }
    }
    private async Task TryRestoreBrowserView()
    {
            if(pendingViewRestore is not {} pending)return;
            var saved=pending.View;long revision=pending.Revision,requestedRoot=pending.RootVersion;
            if(revision!=viewRestoreRevision||requestedRoot!=rootChangeVersion||pending.NavigationVersion!=directoryNavigationRequest||catalog is null||resultHandle is null||results is null)return;
            long query=generation;var handle=resultHandle;var sourceResults=results;
            FilesGrid.Visibility=saved.Details?Visibility.Collapsed:Visibility.Visible;DetailsPane.Visibility=saved.Details?Visibility.Visible:Visibility.Collapsed;
            var list=saved.Details?(ListViewBase)FilesList:FilesGrid;
            long? selectedOrdinal=saved.SelectedPath is {} path?await catalog.FindOrdinal(handle.Id,path,lifetime.Token):null;
            long? anchorOrdinal=saved.ScrollAnchorPath is {Length:>0} anchor?await catalog.FindOrdinal(handle.Id,anchor,lifetime.Token):null;
            if(!ReferenceEquals(pendingViewRestore,pending)||pending.InteractionVersion!=browserInteractionVersion||pending.NavigationVersion!=directoryNavigationRequest||revision!=viewRestoreRevision||requestedRoot!=rootChangeVersion||query!=generation||sourceResults!=results)return;
            // A partial first scan may not yet contain the saved file. Retry on its next snapshot.
            if((scanTask is {IsCompleted:false}||metadataTask is {IsCompleted:false})&&((saved.SelectedPath is not null&&selectedOrdinal is null)||(saved.ScrollAnchorPath is not null&&anchorOrdinal is null)))return;
            bool restoreStart=saved.ScrollOffset<=0&&saved.ScrollAnchorPath is null&&saved.SelectedPath is null;
            pendingViewRestore=null;
            // Restoring an untouched folder is not a scroll gesture. A settling
            // timer would adopt a late native displacement as the user's offset.
            if(restoreStart)ResetBrowserStart();else BeginBrowserMovement();
            if(selectedOrdinal is not null&&saved.Preview is {} preview&&saved.SelectedPath is {} previewPath)pendingPreviewRestore=new(preview,previewPath,requestedRoot);
            if(selectedOrdinal is {} ordinal){var row=(FileRow)sourceResults[(int)ordinal]!;RevealBrowserRow(row);RestoreBrowserSelection(list,row);}
            list.UpdateLayout();
            if((anchorOrdinal??selectedOrdinal) is {} visible)list.ScrollIntoView(sourceResults[(int)visible],ScrollIntoViewAlignment.Leading);
            else if(restoreStart)RestorePublicationViewport(list,0);
            else FindScrollViewer(list)?.ChangeView(null,double.IsFinite(saved.ScrollOffset)?Math.Max(0,saved.ScrollOffset):0,null,true);
            Status.Text="";
    }
    private async Task SaveLastSession(SavedView? captured=null)
    {
        if(settings is null||rootId.Length==0)return;await settings.Save("last-session.json",captured??CaptureView());
    }
    private async Task ManageViews()
    {
        if(settings is null)return;var views=await settings.Load<Dictionary<string,SavedView>>("views.json")??[];
        var pick=new ComboBox{ItemsSource=views.Keys.Order(StringComparer.CurrentCulture).ToArray(),HorizontalAlignment=HorizontalAlignment.Stretch,MinWidth=400};if(views.Count>0)pick.SelectedIndex=0;
        var rename=new TextBox{Header="重命名（留空保持原名）",MaxLength=100};var panel=new StackPanel{Spacing=10};panel.Children.Add(pick);panel.Children.Add(rename);
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="已保存的浏览设置",Content=panel,PrimaryButtonText="应用浏览设置",SecondaryButtonText="删除所选",CloseButtonText="取消",IsPrimaryButtonEnabled=views.Count>0,IsSecondaryButtonEnabled=views.Count>0};
        var choice=await dialog.ShowAsync();if(pick.SelectedItem is not string name||choice==ContentDialogResult.None)return;
        var saved=views[name];string replacement=rename.Text.Trim();
        if(choice==ContentDialogResult.Secondary)
        {
            await settings.Update<Dictionary<string,SavedView>>("views.json",latest=>
            {latest??=[];RequireUnchangedSavedView(latest,name,saved);latest.Remove(name);return latest;});
            Status.Text="浏览设置已删除。";return;
        }
        if(replacement.Length>0&&replacement!=name)
        {
            await settings.Update<Dictionary<string,SavedView>>("views.json",latest=>
            {latest??=[];RequireUnchangedSavedView(latest,name,saved);if(latest.ContainsKey(replacement))throw new InvalidOperationException("同名视图已存在，请使用其他名称。");latest.Remove(name);latest.Add(replacement,saved);return latest;});
            name=replacement;
        }
        await ApplyNamedView(name,saved);
    }
    private static bool SameSavedView(SavedView left,SavedView right)=>System.Text.Json.JsonSerializer.Serialize(left)==System.Text.Json.JsonSerializer.Serialize(right);
    private static void RequireUnchangedSavedView(Dictionary<string,SavedView> views,string name,SavedView expected)
    {if(!views.TryGetValue(name,out var current)||!SameSavedView(current,expected))throw new InvalidOperationException("这项浏览设置已经更改，请重新打开列表。");}
    private async Task ApplyNamedView(string name,SavedView saved)
    {
        SavedView? accepted=null;
        await RestoreSavedView(saved,onViewAccepted:view=>accepted=view);
        if(accepted is null||!saved.Filter.NeedsCollectionRuleBase)return;
        var migrated=accepted;
        await settings!.Update<Dictionary<string,SavedView>>("views.json",latest=>
        {
            latest??=[];
            // Preserve any independent save, rename or deletion made while the
            // accepted collection was still refreshing its source observations.
            if(latest.TryGetValue(name,out var current)&&SameSavedView(current,saved))latest[name]=migrated;
            return latest;
        });
    }
}
