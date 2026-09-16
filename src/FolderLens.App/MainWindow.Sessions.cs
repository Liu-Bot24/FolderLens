using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool restoringView;
    private long viewRestoreRevision;
    private sealed record PendingViewRestore(SavedView View,long Revision,long RootVersion);
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
            MinWidth.Value=filter.Ranges.TryGetValue("width",out var width)?width.Min??double.NaN:double.NaN;MinHeight.Value=filter.Ranges.TryGetValue("height",out var height)?height.Min??double.NaN:double.NaN;
        }
        finally{suppressFilters=false;UpdateDetailSort();}
    }
    private async Task RestoreSavedView(SavedView saved,bool recordHistory=true,bool recheckDirectory=false)
    {
        saved.Filter.Validate();saved=saved with{Filter=saved.Filter.ForBrowserView()};
        var previous=rootId.Length>0?CaptureView():null;
        bool sameRoot=previous is not null&&browserRootReady&&!replacingRoot&&string.Equals(root,saved.Root,StringComparison.Ordinal)&&previous.Filter.HasSameScanPolicy(saved.Filter);
        restoringView=true;long revision=++viewRestoreRevision,requestedRoot=rootChangeVersion+(sameRoot?0:1);
        pendingViewRestore=new(saved,revision,requestedRoot);
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
                    var resumed=await RootTaskRetirement.ResumeForNavigation(stopped,scanTask,metadataTask,lifetime.Token);
                    if(closing||revision!=viewRestoreRevision||requestedRoot!=rootChangeVersion||cancelRevision!=scanCancelRevision||!ReferenceEquals(scanStop,stopped)){resumed.Dispose();return;}
                    scanStop=resumed;
                    stopped.Dispose();
                }
                await new FolderLens.Infrastructure.ScanDirtyDirectories(catalog!).Mark(rootId,epoch,[new(saved.Filter.DirectoryScope,"BrowseNavigation",true)],lifetime.Token);
                if(closing||revision!=viewRestoreRevision||requestedRoot!=rootChangeVersion)return;
                reconcilePending=true;PreferScanDirectory(rootId,saved.Filter.DirectoryScope);
            }
            ApplySavedFilter(saved.Filter);RootPath.Text=activeCollectionId is null?Path.Combine(saved.Root,saved.Filter.DirectoryScope):"收藏夹："+CollectionLabel(activeCollectionId);DetailsMode.IsChecked=saved.Details;UpdatePathPresentationControl();
            if(sameRoot)
            {
                if(activeCollectionId is null)
                {
                    ShowTreeRoot(BrowsedDirectory);
                    if(activeTreeRoot is {} node)node.Content=new FolderNode(BrowsedDirectory,(node.Content as FolderNode)?.Label??FolderLabel(BrowsedDirectory),rootId,saved.Filter.DirectoryScope,root,Icon:(node.Content as FolderNode)?.Icon);
                    QueueTreeRefresh();
                }
                if(recordHistory&&previous is not null)navigationHistory.VisitFrom(previous);UpdateNavigationButtons();
                await RefreshQuery(preserveViewport:false);
            }
            else await OpenRoot(saved.Root,recordHistory:recordHistory,previousView:previous,preserveDirectoryScope:true);
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
                if(activeCollectionId is not null&&requestedRoot==rootChangeVersion&&!closing)_=StartMetadataRefresh();
                if(reconcilePending&&requestedRoot==rootChangeVersion&&!closing)_=Reconcile();
            }
        }
    }
    private async Task TryRestoreBrowserView()
    {
            if(pendingViewRestore is not {} pending)return;
            var saved=pending.View;long revision=pending.Revision,requestedRoot=pending.RootVersion;
            if(revision!=viewRestoreRevision||requestedRoot!=rootChangeVersion||catalog is null||resultHandle is null||results is null)return;
            long query=generation;var handle=resultHandle;var sourceResults=results;
            FilesGrid.Visibility=saved.Details?Visibility.Collapsed:Visibility.Visible;DetailsPane.Visibility=saved.Details?Visibility.Visible:Visibility.Collapsed;
            var list=saved.Details?(ListViewBase)FilesList:FilesGrid;
            long? selectedOrdinal=saved.SelectedPath is {} path?await catalog.FindOrdinal(handle.Id,path,lifetime.Token):null;
            long? anchorOrdinal=saved.ScrollAnchorPath is {Length:>0} anchor?await catalog.FindOrdinal(handle.Id,anchor,lifetime.Token):null;
            if(revision!=viewRestoreRevision||requestedRoot!=rootChangeVersion||query!=generation||sourceResults!=results)return;
            // A partial first scan may not yet contain the saved file. Retry on its next snapshot.
            if((scanTask is {IsCompleted:false}||metadataTask is {IsCompleted:false})&&((saved.SelectedPath is not null&&selectedOrdinal is null)||(saved.ScrollAnchorPath is not null&&anchorOrdinal is null)))return;
            pendingViewRestore=null;
            if(selectedOrdinal is not null&&saved.Preview is {} preview&&saved.SelectedPath is {} previewPath)pendingPreviewRestore=new(preview,previewPath,requestedRoot);
            if(selectedOrdinal is {} ordinal){var row=(FileRow)sourceResults[(int)ordinal]!;RevealBrowserRow(row);list.SelectedItem=row;}
            list.UpdateLayout();
            if((anchorOrdinal??selectedOrdinal) is {} visible)list.ScrollIntoView(sourceResults[(int)visible],ScrollIntoViewAlignment.Leading);
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
        if(choice==ContentDialogResult.Secondary){views.Remove(name);await settings.Save("views.json",views);Status.Text="浏览设置已删除。";return;}
        var saved=views[name];string replacement=rename.Text.Trim();
        if(replacement.Length>0&&replacement!=name){if(views.ContainsKey(replacement))throw new InvalidOperationException("同名视图已存在，请使用其他名称。");views.Remove(name);views.Add(replacement,saved);await settings.Save("views.json",views);}
        await RestoreSavedView(saved);
    }
}
