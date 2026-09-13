using System.Diagnostics;
using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool controlsReady, suppressFilters, immersive, resizingPane;
    private CancellationTokenSource? slideTickStop;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? searchTimer, slideTimer;
    private string? playerExecutable;
    private bool playerSupportsPlaylists;
    private readonly Dictionary<FileRow,CancellationTokenSource> thumbnailRequests=[];
    private readonly HashSet<FileRow> propertyRequests=[];
    private readonly HashSet<FileRow> propertyRefreshPending=[];
    private readonly Dictionary<(ListViewBase View,DependencyObject Container),FileRow> visibleContainers=[];
    private readonly Dictionary<FileRow,int> visibleConsumerCounts=[];
    private readonly SemaphoreSlim thumbnailSlots=new(FolderLens.Infrastructure.WorkerResources.Shared.ThumbnailConcurrency,FolderLens.Infrastructure.WorkerResources.Shared.ThumbnailConcurrency);
    // Bound metadata tails as well as active decoders; slow writes cannot create
    // an unbounded backlog of completed image requests on smaller machines.
    private readonly SemaphoreSlim thumbnailPipelines=new(FolderLens.Infrastructure.WorkerResources.Shared.ThumbnailConcurrency*2,FolderLens.Infrastructure.WorkerResources.Shared.ThumbnailConcurrency*2);
    private readonly List<FolderLens.Infrastructure.WorkerClient> extraThumbnailWorkers=[];
    private int thumbnailWorkCount;
    private readonly TaskCompletionSource thumbnailsIdle=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record FolderNode(string Path,string Label,string? CatalogRoot=null,string Relative="",string BasePath="",long? PageOffset=null)
    { public override string ToString()=>Label; }
    private sealed record DesktopState(double ThumbnailSize=144,bool ShowPaths=false,double SidebarWidth=300,bool Details=false);

    private void InitializeDesktop()
    {
        if(!Environment.GetCommandLineArgs().Contains("--verify-refresh")&&AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)presenter.Maximize();
        searchTimer=DispatcherQueue.CreateTimer();searchTimer.Interval=TimeSpan.FromMilliseconds(280);searchTimer.IsRepeating=false;searchTimer.Tick+=async(_,_)=>await RefreshQuery();
        slideTimer=DispatcherQueue.CreateTimer();slideTimer.Interval=TimeSpan.FromSeconds(5);slideTimer.IsRepeating=false;
        slideTimer.Tick+=async(_,_)=>
        {
            if(slideTickStop is {IsCancellationRequested:false}||closing||!slideShow||results is null||selected is null)return;
            using var operation=browserWork.Enter();if(operation is null)return;
            using var tick=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,selectionStop.Token);slideTickStop=tick;
            var source=results;var origin=selected;long request=slideRequest,selectedRequest=browserSelectionRequest;
            bool IsCurrent()=>!tick.IsCancellationRequested&&!closing&&slideShow&&request==slideRequest&&selectedRequest==browserSelectionRequest&&ReferenceEquals(results,source)&&ReferenceEquals(selected,origin);
            try
            {
                for(long index=origin.Ordinal+1;index<source.Count;index++)
                {
                    var row=(FileRow)source[(int)index]!;if(verifySlideAdvanceBarrier is not null)await verifySlideAdvanceBarrier(tick.Token);if(!IsCurrent())return;await source.EnsureLoaded(row,tick.Token);
                    if(!IsCurrent())return;
                    if(row.Kind!="image")continue;
                    Navigate(checked((int)(index-origin.Ordinal)));return;
                }
                if(IsCurrent()){slideShow=false;Status.Text="幻灯片已到末尾。";}
            }
            catch(OperationCanceledException){}catch(Exception ex){if(IsCurrent()){ShowError(ex);slideShow=false;}else RecordWebView("Retired slideshow tick failed: "+ex.GetType().Name);}
            finally{if(ReferenceEquals(slideTickStop,tick))slideTickStop=null;verifySlideTickCompleted?.Invoke();}
        };
        ImageCanvas.SizeChanged+=(_,_)=>ImageCanvas.Invalidate();
        FilesGrid.SizeChanged+=(_,_)=>UpdateGridPresentation();
        FilesGrid.Loaded+=(_,_)=>UpdateGridPresentation();
        BrowserPane.SizeChanged+=(_,_)=>UpdateBrowserToolbar();
        InitializeFullScreen();
        InitializePreviewState();
        InitializeMarkdownLifetime();
        InitializeProperties();
        InitializeDetails();
        controlsReady=true;
    }
    private bool slideShow;
    private async Task ReceiveActivations()
    {
        try{await InstanceBroker!.Run(request=>
        {
            DispatcherQueue.TryEnqueue(async()=>
            {
                if(closing)return;Activate();
                try{if(!string.IsNullOrEmpty(request.File))await OpenPath(request.File);else if(!string.IsNullOrEmpty(request.Root)){RootPath.Text=request.Root;await OpenRoot(request.Root);}}
                catch(Exception ex){ShowError(ex);}
            });return Task.CompletedTask;
        },lifetime.Token);}catch(Exception ex){DispatcherQueue.TryEnqueue(()=>ShowError(ex));}
    }
    private ExclusionSpec[] ScanExclusions()
    {
        var exclusions=(advanced?.Exclusions??[]).ToList();string relative=Path.GetRelativePath(root,dataDirectory);
        if(relative!="."&&!Path.IsPathRooted(relative)&&!relative.Split('\\','/').Contains(".."))exclusions.Add(new(relative,"skipScan"));
        return exclusions.ToArray();
    }
    private async Task RestoreDesktop()
    {
        if(settings is null)return;
        await RestoreDetailWidths();
        if(await settings.Load<DesktopState>("desktop.json") is {} state)
        {
            ThumbnailSize.Value=Math.Clamp(state.ThumbnailSize,100,240);ShowPaths.IsChecked=state.ShowPaths;
            PreviewColumn.Width=new GridLength(Math.Clamp(state.SidebarWidth,220,650));DetailsMode.IsChecked=state.Details;ToggleView(this,new());
        }
        await RestorePlayerPreferences();
        try
        {
            var cleanup=await Task.Run(()=>FolderLens.Infrastructure.PlaylistFiles.Cleanup(Path.Combine(dataDirectory,"playlists"),DateTimeOffset.UtcNow,lifetime.Token),lifetime.Token);
            if(cleanup.Removed>0||cleanup.Failed>0||cleanup.Incomplete)RecordWebView($"Playlist cleanup removed={cleanup.Removed} failed={cleanup.Failed} incomplete={cleanup.Incomplete}");
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){RecordWebView($"Playlist cleanup failed: {ex.GetType().Name}");}
        await RestoreViewerPreferences();
    }
    private readonly Dictionary<string,bool> categoryDetailViews=[];
    private async void QuickFilterChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!controlsReady||suppressFilters)return;
        if(ReferenceEquals(sender,Category))
        {
            string category=Tag(Category);
            bool details=categoryDetailViews.TryGetValue(category,out bool preferred)?preferred:category is not ("image" or "video" or "media");
            if((DetailsMode.IsChecked==true)!=details){DetailsMode.IsChecked=details;ToggleView(this,new());}
            UpdateSortOptions();
            ApplyDetailColumns();
        }
        await RefreshQuery();
    }
    private void SearchChanged(object sender,TextChangedEventArgs e){if(controlsReady&&!suppressFilters){searchTimer?.Stop();searchTimer?.Start();}}
    private async void SearchKeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;searchTimer?.Stop();await RefreshQuery();}}
    private int browserToolbarLayout=-1;
    private void UpdateBrowserToolbar()
    {
        int layout=BrowserPane.ActualWidth<500?2:BrowserPane.ActualWidth<900?1:0;
        if(browserToolbarLayout==layout)return;browserToolbarLayout=layout;
        bool narrow=layout==2,compact=layout==1;
        BrowserToolbar.RowSpacing=layout==0?0:6;
        BrowserToolbar.RowDefinitions.Clear();for(int i=0;i<(narrow?4:compact?2:1);i++)BrowserToolbar.RowDefinitions.Add(new(){Height=GridLength.Auto});
        for(int i=0;i<BrowserToolbar.ColumnDefinitions.Count;i++)BrowserToolbar.ColumnDefinitions[i].Width=narrow||i==3?new GridLength(1,GridUnitType.Star):GridLength.Auto;
        void Place(FrameworkElement element,int row,int column,int span=1){Grid.SetRow(element,row);Grid.SetColumn(element,column);Grid.SetColumnSpan(element,span);}
        Category.Width=narrow?double.NaN:compact?110:142;SortField.Width=narrow?double.NaN:compact?110:132;
        Place(Category,0,0,narrow?3:1);Place(SortField,0,narrow?3:1,narrow?4:1);
        Place(SortGroupingToolbar,narrow?1:0,narrow?0:2,narrow?7:compact?5:1);
        Place(Search,narrow?2:compact?1:0,layout==0?3:0,narrow?7:compact?4:1);
        Place(QuickFiltersButton,narrow?3:compact?1:0,narrow?0:4,narrow?2:1);
        Place(DetailsMode,narrow?3:compact?1:0,narrow?2:5,narrow?2:1);
        Place(LargePreviewButton,narrow?3:compact?1:0,narrow?4:6,narrow?3:1);
    }
    private double GridCardWidth
    {
        get
        {
            double viewport=FindScrollViewer(FilesGrid)?.ViewportWidth??FilesGrid.ActualWidth;
            if(!double.IsFinite(viewport)||viewport<=10)return ThumbnailSize.Value;
            double available=Math.Max(1,viewport-FilesGrid.Padding.Left-FilesGrid.Padding.Right-2);
            int columns=Math.Max(1,(int)Math.Floor(available/(ThumbnailSize.Value+4)));
            double scale=Shell.XamlRoot?.RasterizationScale??1;
            return Math.Max(1,Math.Floor((available/columns-4)*scale)/scale);
        }
    }
    private void UpdateGridPresentation()
    {
        if(!controlsReady||FilesGrid.ActualWidth<=10)return;
        double width=GridCardWidth;
        if(FilesGrid.ItemsPanelRoot is ItemsWrapGrid panel)panel.ItemWidth=width+4;
        results?.SetPresentation(width,ShowPaths.IsChecked==true);
        if(FilesGrid.ItemsSource is FileRow[] initial)
            foreach(var row in initial)row.SetPresentation(width,ShowPaths.IsChecked==true);
    }
    private void PresentationChanged(object sender,RoutedEventArgs e)=>UpdateGridPresentation();
    private void ThumbnailSizeChanged(object sender,RangeBaseValueChangedEventArgs e)=>UpdateGridPresentation();
    private async void ResetFilters(object sender,RoutedEventArgs e)
        =>await ResetBrowserFilters();
    private async Task ResetBrowserFilters()
    {
        suppressFilters=true;advanced=null;Search.Text="";Formats.Text="";RawMode.SelectedIndex=0;AnimationMode.SelectedIndex=0;SearchPath.IsChecked=false;ShowHidden.IsChecked=false;PendingView.IsChecked=false;
        MinSize.Value=MaxSize.Value=MinWidth.Value=MinHeight.Value=double.NaN;suppressFilters=false;FilterFlyout.Hide();await ApplyBrowserFilters();
    }
    private async void ParentRoot(object sender,RoutedEventArgs e){var parent=Directory.GetParent(root);if(parent is not null){RootPath.Text=parent.FullName;await OpenRoot(parent.FullName);}}
    private void StartPaneResize(object sender,PointerRoutedEventArgs e){resizingPane=true;PaneDivider.CapturePointer(e.Pointer);}
    private void ResizePane(object sender,PointerRoutedEventArgs e){if(resizingPane&&!immersive)PreviewColumn.Width=new GridLength(Math.Clamp(e.GetCurrentPoint(BodyGrid).Position.X,220,Math.Max(220,Math.Min(650,BodyGrid.ActualWidth*.5))));}
    private void EndPaneResize(object sender,PointerRoutedEventArgs e){resizingPane=false;PaneDivider.ReleasePointerCapture(e.Pointer);}
    private async void TogglePreview(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.ToggleWindowViewer);
    private async void CloseExpandedPreview(object sender,RoutedEventArgs e)=>await ReturnToBrowser();
    private async Task SetImmersive(bool enabled)
    {
        if(enabled&&selected is null||!enabled&&!immersive)return;if(enabled&&!immersive)CaptureBrowserPosition();immersive=enabled;
        TreePane.Visibility=BrowserPane.Visibility=PaneDivider.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        Grid.SetRow(PreviewPane,enabled?0:1);Grid.SetRowSpan(PreviewPane,enabled?2:1);Grid.SetColumnSpan(PreviewPane,enabled?3:1);
        UpdateViewerInformation();PreviewPane.UpdateLayout();ImageCanvas.Invalidate();
        if(!enabled){RestoreBrowserPosition();return;}
        if(selected is not null&&!previewLoading&&selected.Kind=="image")
        {
            long current=selection;try{if(zoom>0)await LoadVisibleTiles();else if(!animationRunning)await EnsureFitResolution(selected,current,selectionStop.Token);}catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}
        }
    }
    private async void ToggleFullScreen(object sender,RoutedEventArgs e)
    {
        await RunViewerAction(ViewerAction.ToggleFullScreen);
    }
    private async void ToggleSlideshow(object sender,RoutedEventArgs e)
    {
        try{await ToggleSlideshowCore();}finally{verifySlideEventCompleted?.Invoke();}
    }
    private long slideRequest;
    private async Task ToggleSlideshowCore()
    {
        long request=++slideRequest;slideTickStop?.Cancel();
        try
        {
        slideShow=!slideShow;if(!slideShow){browserSelectionRequest++;slideTimer?.Stop();Status.Text="幻灯片已暂停。";return;}
        if(selected is null&&results is {Count:>0} source&&!await SelectBrowserOrdinal(source,0,queryStop.Token))
        {if(request==slideRequest){slideShow=false;slideTimer?.Stop();}return;}
        if(request!=slideRequest||closing||!slideShow)return;
        if(selected is null||selected.Kind!="image"){slideShow=false;Status.Text="请选择一张图片开始幻灯片。";return;}
        await SetImmersive(true);
        if(request!=slideRequest||closing||!slideShow)return;
        slideTimer?.Start();Status.Text="幻灯片 · 每张图片停留 5 秒 · Ctrl+Space 暂停";
        }
        catch(OperationCanceledException){if(request==slideRequest){slideShow=false;slideTimer?.Stop();}}
        catch(Exception error)
        {
            if(request==slideRequest&&!closing){slideShow=false;slideTimer?.Stop();ShowError(error);}
            else RecordWebView("Retired slideshow failed: "+error.GetType().Name);
        }
    }
    private async void InvokeFolder(TreeView sender,TreeViewItemInvokedEventArgs e)
    {
        try{var node=e.InvokedItem as TreeViewNode;var folder=node?.Content as FolderNode??e.InvokedItem as FolderNode;
        if(folder?.PageOffset is {} offset&&node?.Parent is {} parent){await ChangeTreePage(parent,offset);return;}
        if(folder is not null)await NavigateFolder(folder);}
        catch(OperationCanceledException){}catch(Exception error){ShowError(error);}
    }
    private async Task NavigateFolder(FolderNode folder)
    {if(folder.PageOffset is not null||string.Equals(folder.Path,root,StringComparison.Ordinal))return;RootPath.Text=folder.Path;await OpenRoot(folder.Path);}
    private void FilesDragOver(object sender,DragEventArgs e){if(e.DataView.Contains(StandardDataFormats.StorageItems)){e.AcceptedOperation=DataPackageOperation.Copy;e.DragUIOverride.Caption="打开并查看";}}
    private async void FilesDrop(object sender,DragEventArgs e)
    {
        if(!e.DataView.Contains(StandardDataFormats.StorageItems))return;try{var items=await e.DataView.GetStorageItemsAsync();if(items.FirstOrDefault() is {} item)await OpenPath(item.Path);}catch(Exception ex){ShowError(ex);}
    }
    private async Task OpenPath(string path)
    {
        if(Directory.Exists(path)){RootPath.Text=path;await OpenRoot(path);return;}
        string parent=Path.GetDirectoryName(Path.GetFullPath(path))!;suppressFilters=true;SelectTag(Category,"all");Search.Text="";advanced=null;suppressFilters=false;RootPath.Text=parent;await OpenRoot(parent);
        if(closing||catalog is null||resultHandle is not {} handle||results is not {} source||!string.Equals(root,parent,StringComparison.OrdinalIgnoreCase))return;
        long rootVersion=rootChangeVersion;var token=queryStop.Token;
        long? ordinal=await catalog.FindOrdinal(handle.Id,Path.GetFileName(path),token);
        if(closing||token.IsCancellationRequested||rootVersion!=rootChangeVersion||!ReferenceEquals(resultHandle,handle)||!ReferenceEquals(results,source))return;
        if(ordinal is >=0&&ordinal<source.Count)await SelectBrowserOrdinal(source,(int)ordinal.Value,token);
    }
    private async void CopyFileReference(object sender,RoutedEventArgs e)
    {
        if(selected is null)return;try{var file=await StorageFile.GetFileFromPathAsync(Path.Combine(root,selected.RelativePath));var data=new DataPackage();data.SetStorageItems([file],true);data.RequestedOperation=DataPackageOperation.Copy;Clipboard.SetContent(data);}catch(Exception ex){ShowError(ex);}
    }
    private void UpdatePlaylistCommand()=>PlaylistMenu.IsEnabled=playerSupportsPlaylists&&!string.IsNullOrWhiteSpace(playerExecutable);
    private void StartPlayer(string path)
    {
        Process.Start(FolderLens.Infrastructure.ExternalFileLaunch.CreateStartInfo(path,playerExecutable,AppContext.BaseDirectory,dataDirectory));
    }
    private async void PlayResultVideos(object sender,RoutedEventArgs e)
    {
        if(catalog is not {} store||resultHandle is not {} handle||!playerSupportsPlaylists||string.IsNullOrWhiteSpace(playerExecutable))return;
        string sourceRoot=root,directory=Path.Combine(dataDirectory,"playlists");long currentOrdinal=selected?.Ordinal??0;bool retained=false,busy=false;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var chosen=SelectedOrdinals(DetailsMode.IsChecked==true?FilesList:FilesGrid);long chosenCount=chosen.Sum(range=>range.Count);
        string[] scopes=chosenCount>0?["当前结果的全部视频","从当前项开始的后续视频",$"仅选中项目中的视频（已选 {chosenCount:N0} 项）"]:["当前结果的全部视频","从当前项开始的后续视频"];
        var scope=new ComboBox{Header="播放范围",ItemsSource=scopes,SelectedIndex=chosenCount>1?2:0,MinWidth=440};
        var note=new TextBlock{Text="按当前列表顺序播放，一次最多 10,000 个视频。超过上限请缩小筛选或选择范围。",TextWrapping=TextWrapping.Wrap,MaxWidth=480};
        var panel=new StackPanel{Spacing=12};panel.Children.Add(scope);panel.Children.Add(note);
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="播放筛选结果",Content=panel,PrimaryButtonText="开始播放",CloseButtonText="关闭"};
        Task operation=Task.CompletedTask;
        async Task GeneratePlaylist()
        {
            if(busy)return;busy=true;dialog.IsPrimaryButtonEnabled=false;scope.IsEnabled=false;
            try
            {
                long start=scope.SelectedIndex==1?currentOrdinal:0;
                var selectedRanges=scope.SelectedIndex==2?chosen:null;
                var batch=await Task.Run(()=>FolderLens.Infrastructure.MediaTools.Playlist(store,handle,sourceRoot,directory,start,cancel.Token,selection:selectedRanges),cancel.Token);
                if(cancel.IsCancellationRequested||closing)return;
                StartPlayer(batch.Path);
                Status.Text=$"已打开 {batch.Count:N0} 个视频（结果位置 {batch.FirstOrdinal+1:N0}–{batch.LastOrdinal+1:N0}）。";
                dialog.Hide();
            }
            catch(OperationCanceledException){note.Text="已取消生成播放列表。";}
            catch(Exception ex){note.Text=$"无法播放：{ex.Message}";dialog.IsPrimaryButtonEnabled=true;scope.IsEnabled=true;}
            finally{busy=false;}
        }
        dialog.PrimaryButtonClick+=(_,args)=>{args.Cancel=true;if(!busy)operation=GeneratePlaylist();};
        dialog.CloseButtonClick+=(_,_)=>cancel.Cancel();
        try
        {
            retained=await store.RetainSnapshot(handle.Id,lifetime.Token);if(!retained)throw new InvalidOperationException("结果已过期，请重新应用筛选。");
            await dialog.ShowAsync();
        }
        catch(Exception ex){ShowError(ex);}
        finally{cancel.Cancel();await operation;if(retained)await store.ReleaseSnapshot(handle.Id);}
    }
    private async void ShowDiagnostics(object sender,RoutedEventArgs e)
    {
        var statistics=thumbnailCache is null?null:await thumbnailCache.GetStatistics(lifetime.Token);
        var text=new TextBlock{Text=$"数据位置：{dataDirectory}\n{Environment.Version} · Windows x64\n当前目录：{root}\n当前结果：{resultHandle?.Count??0:N0}\n缩略图缓存：{FileRow.FormatBytes(statistics?.TotalDiskBytes??0)} · {statistics?.DiskEntries??0:N0} 项\n清理缓存不会更改原文件。",TextWrapping=TextWrapping.Wrap,MaxWidth=560};
        var open=new Button{Content="打开数据文件夹"};open.Click+=(_,_)=>Process.Start(new ProcessStartInfo(dataDirectory){UseShellExecute=true});var panel=new StackPanel{Spacing=12};panel.Children.Add(text);panel.Children.Add(open);
        var capabilityText=new TextBlock{Text=capabilityStatus,TextWrapping=TextWrapping.Wrap,MaxWidth=560};var refreshCapabilities=new Button{Content="重新检测媒体组件"};
        refreshCapabilities.Click+=async(_,_)=>{refreshCapabilities.IsEnabled=false;try{if(capabilityTask is not null)await capabilityTask;capabilityTask=ReadRuntimeCapabilities();await capabilityTask;capabilityText.Text=capabilityStatus;}finally{refreshCapabilities.IsEnabled=true;}};
        panel.Children.Add(new ScrollViewer{Content=capabilityText,MaxHeight=280,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});panel.Children.Add(refreshCapabilities);
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="缓存与诊断",Content=panel,PrimaryButtonText="清理缩略图缓存",CloseButtonText="关闭"};if(await dialog.ShowAsync()==ContentDialogResult.Primary&&thumbnailCache is not null)try{var cleaned=await thumbnailCache.Clear(lifetime.Token);Status.Text=$"已清理 {cleaned.RemovedEntries:N0} 项缓存；正在使用的项目将保留。";}catch(Exception ex){ShowError(ex);}
    }
}
