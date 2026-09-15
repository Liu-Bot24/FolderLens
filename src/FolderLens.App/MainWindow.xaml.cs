using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.Web.WebView2.Core;

namespace FolderLens.App;

public sealed partial class MainWindow : Window
{
    internal SingleInstanceBroker? InstanceBroker {get;set;}
    internal string? InitialDataDirectory {get;set;}
    private readonly SemaphoreSlim rootChangeGate=new(1,1);
    private readonly WorkRetirement browserWork=new();
    private FilterSpec? scannedPolicy,lastAppliedFilter;
    private long rootChangeVersion;
    private bool replacingRoot;
    private CatalogStore? catalog;
    private WorkerClient? previewWorker,thumbnailWorker,secondThumbnailWorker,metadataWorker;
    private readonly Queue<WorkerClient> thumbnailPool=[];
    private ThumbnailCache? thumbnailCache;
    private string providerIdentity="";
    private Task? metadataTask;
    private VirtualResults? results;
    private ResultHandle? resultHandle;
    private CancellationTokenSource scanStop=new(),queryStop=new(),selectionStop=new(),lifetime=new();
    private Task? scanTask;
    private string root="",rootId="",dataDirectory="";
    private BrowsingSessionStorage? browsingStorage;
    private string RuntimeDataDirectory=>browsingStorage?.DirectoryPath??dataDirectory;
    private long epoch,generation,selection,queryRequest;
    private bool updatingBrowser;
    private FileRow? selected;
    private CanvasBitmap? fitBitmap;
    private readonly Dictionary<(int,int),CanvasBitmap> tiles=[];
    private readonly SemaphoreSlim tileGate=new(1,1);
    private bool tileReloadPending;
    private double sourceWidth,sourceHeight,zoom;
    private Vector2 pan;
    private int rotation;
    private long textStart,textNext;
    private bool closing,queryBusy;
    private readonly ScanPreviewRefresh scanPreviewRefresh=new();
    private bool rawPreviewOnly;
    private readonly HashSet<FileRow> visible=[];
    private MediaPlayer? audio;
    private MediaTools? media;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? animationTimer,audioTimer;
    private bool animationRunning,audioUpdating;
    private bool animationLoading;
    private TaskCompletionSource? animationIdle;
    private bool animationNeedsOpen=true;
    private int animationFrameIndex;
    private int animationNextFrame;
    private long animationCompletedLoops,animationRevision;
    private bool animationCompleted;
    private long animationDeadline;
    private CanvasBitmap? pendingAnimationBitmap;
    private JsonElement pendingAnimationData;
    private int imagePage,imagePageCount=1;
    private int? requestedImagePage;
    private WorkerClient? contentWorker;
    private WebView2? markdown;
    private readonly Dictionary<string,byte[]> markdownImages=[];
    private string? textEncoding;
    private FilterSpec? advanced;
    private AtomicSettings? settings;
    private RootChangeMonitor? monitor;
    private bool reconcilePending;
    private readonly List<string> webviewEvents=[];
    private TaskCompletionSource<bool>? navigationComplete;
    private string markdownDocumentUrl="";
    private byte[] markdownDocument=[];
    private ulong markdownNavigationId;

    public MainWindow()
    {
        StartStartupMeasurements();
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","FolderLens.ico"));
        InitializeDesktop();
        InitializeBrowserEmptyState();
        AppWindow.Closing+=OnClosing;
        Shell.Loaded+=async(_,_)=>await Initialize();
        animationTimer=DispatcherQueue.CreateTimer();animationTimer.IsRepeating=false;animationTimer.Tick+=async(_,_)=>await AdvanceAnimation(false);
        AppWindow.Changed+=(_,_)=>{if(!AppWindow.IsVisible||AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter {State:Microsoft.UI.Windowing.OverlappedPresenterState.Minimized})PauseAnimationForDetail();};
        InitializeAudio();
        InitializeMarquee();
        WorkerResources.Shared.MemoryPressure+=OnPrefetchMemoryPressure;
        StartupStage("constructWindow");
    }
    private async Task Initialize()
    {
        if(startupClock is not null)startupShellLoadedAt=startupClock.Elapsed.TotalMilliseconds;
        using var initialization=browserWork.Enter();if(initialization is null||closing)return;
        try
        {
            string[] args=Environment.GetCommandLineArgs();bool deployedVerification=args.Contains("--verify-refresh")&&args.Contains("--verify-deployed");dataDirectory=InitialDataDirectory??await AppPaths.DataDirectory(args);
            await InitializeNavigationTree();
            StartupStage("navigation");
            if(args.Contains("--verify-refresh")&&args.Contains("--verify-navigation-roots"))VerifyInitialNavigation();
            browsingStorage=await BrowsingSessionStorage.Open(dataDirectory,lifetime.Token);
            catalog=browsingStorage.Catalog;
            if(closing)return;
            StartupStage("catalog");
            settings=new AtomicSettings(Path.Combine(dataDirectory,"config"));InitializeScanDiagnostics();
            await RefreshCollectionsTree();
            await RestoreDesktop();
            StartupStage("desktopAndCollections");
            string worker=Path.Combine(AppContext.BaseDirectory,"workers","FolderLens.Media.Worker.exe");
            if(!deployedVerification&&!File.Exists(worker))
            {
                var current=new DirectoryInfo(AppContext.BaseDirectory);
                while(current is not null && !File.Exists(Path.Combine(current.FullName,"FolderLens.slnx")))current=current.Parent;
                if(current is not null)worker=Path.Combine(current.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
            }
            verificationComponents["media"]=Path.GetRelativePath(AppContext.BaseDirectory,worker);
            previewWorker=new(worker,Path.Combine(RuntimeDataDirectory,"temp","preview"));thumbnailWorker=new(worker,Path.Combine(RuntimeDataDirectory,"temp","thumbnails"),WorkerPriority.Visible);metadataWorker=new(worker,Path.Combine(RuntimeDataDirectory,"temp","metadata"),WorkerPriority.Metadata);prefetchWorker=new(worker,Path.Combine(RuntimeDataDirectory,"temp","prefetch"),WorkerPriority.Prefetch);
            secondThumbnailWorker=new(worker,Path.Combine(RuntimeDataDirectory,"temp","thumbnails-2"),WorkerPriority.Visible);
            thumbnailPool.Enqueue(thumbnailWorker);thumbnailPool.Enqueue(secondThumbnailWorker);
            for(int i=2;i<WorkerResources.Shared.ThumbnailConcurrency;i++)
            {
                var extra=new WorkerClient(worker,Path.Combine(RuntimeDataDirectory,"temp","thumbnails-"+(i+1)),WorkerPriority.Visible);
                extraThumbnailWorkers.Add(extra);thumbnailPool.Enqueue(extra);
            }
            thumbnailCache=new(Path.Combine(dataDirectory,"cache","thumbnails"));await thumbnailCache.Initialize(lifetime.Token);
            StartupStage("workersAndCache");
            providerIdentity=await thumbnailWorker.GetProviderIdentity(lifetime.Token);
            StartupStage("providerIdentity");
            capabilityTask=ReadRuntimeCapabilities();
            string contentExecutable=Path.Combine(AppContext.BaseDirectory,"content-worker","FolderLens.Content.Worker.exe");var contentRoot=new DirectoryInfo(AppContext.BaseDirectory);while(contentRoot is not null&&!File.Exists(Path.Combine(contentRoot.FullName,"FolderLens.slnx")))contentRoot=contentRoot.Parent;if(!deployedVerification&&!File.Exists(contentExecutable)&&contentRoot is not null)contentExecutable=Path.Combine(contentRoot.FullName,"src","FolderLens.Content.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Content.Worker.exe");contentWorker=new(contentExecutable,Path.Combine(RuntimeDataDirectory,"temp","markdown"));
            verificationComponents["content"]=Path.GetRelativePath(AppContext.BaseDirectory,contentExecutable);
            verificationComponents["scan"]=Path.GetRelativePath(AppContext.BaseDirectory,ScanWorkerClient.FindExecutable()??Path.Combine(AppContext.BaseDirectory,"scan-worker","FolderLens.Scan.Worker.exe"));
            string native=Path.Combine(AppContext.BaseDirectory,"native","ffmpeg");var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;if(!deployedVerification&&!Directory.Exists(native)&&project is not null)native=Path.Combine(project.FullName,"native","ffmpeg");media=new(Path.Combine(native,"ffprobe.exe"),Path.Combine(native,"ffmpeg.exe"));
            verificationComponents["ffprobe"]=Path.GetRelativePath(AppContext.BaseDirectory,Path.Combine(native,"ffprobe.exe"));
            if(InstanceBroker is not null)_=ReceiveActivations();
            StartupStage("runtimeSetup");
            if(args.Contains("--verify-refresh")){initialization.Dispose();await VerifyRefresh();return;}
            int openIndex=Array.IndexOf(args,"--open"),rootIndex=Array.IndexOf(args,"--root");if(openIndex>=0&&openIndex+1<args.Length)await OpenPath(args[openIndex+1]);else if(rootIndex>=0 && rootIndex+1<args.Length){RootPath.Text=args[rootIndex+1];await OpenRoot(RootPath.Text);}else if(await settings.Load<SavedView>("last-session.json") is {} session)await RestoreSavedView(session);else if(await settings.Load<string>("last-root.json") is {} last){RootPath.Text=last;await OpenRoot(last);}
        }
        catch(Exception ex)
        {
            if(Environment.GetCommandLineArgs().Contains("--verify-refresh"))
            {
                initialization.Dispose();
                await FailVerificationInitialization(ex);
            }
            else ShowBrowserError(ex);
        }
    }
    private static string Tag(ComboBox box)=>((ComboBoxItem)box.SelectedItem).Tag.ToString()!;
    private FilterSpec CurrentFilter()
    {
        var ranges=advanced is null?new Dictionary<string,IntRange>():new Dictionary<string,IntRange>(advanced.Ranges);
        ranges.Remove("logicalBytes");
        long? min=double.IsNaN(MinSize.Value)?null:checked((long)(MinSize.Value*1024*1024));long? max=double.IsNaN(MaxSize.Value)?null:checked((long)(MaxSize.Value*1024*1024));
        if(min is not null || max is not null)ranges["logicalBytes"]=new(min,max);
        if(!double.IsNaN(MinWidth.Value))ranges["width"]=new(checked((long)MinWidth.Value),ranges.GetValueOrDefault("width")?.Max);
        if(!double.IsNaN(MinHeight.Value))ranges["height"]=new(checked((long)MinHeight.Value),ranges.GetValueOrDefault("height")?.Max);
        string category=Tag(Category);
        var filter=(advanced??new FilterSpec()) with{RootId=rootId,ObservedRootEpoch=activeCollectionId is null?epoch:null,CollectionId=activeCollectionId,IncludeCollections=includedCollectionIds.ToArray(),ExcludeCollections=excludedCollectionIds.ToArray(),Kinds=FileCategories.Kinds(category),Extensions=FileCategories.Extensions(category),IncludePending=PendingView.IsChecked==true,Recursive=true,MaxFolderLevels=activeCollectionId is null?browseDepth:null,Raw=Tag(RawMode),Animation=Tag(AnimationMode),ShowHidden=ShowHidden.IsChecked==true,NamePathQuery=Search.Text,SearchScope="name",Formats=savedContentFormats.ToArray(),FileExtensions=selectedFileExtensions.ToArray(),Ranges=ranges,Grouping=folderGrouping,Sort=new(Tag(SortField),sortDescending?"desc":"asc")};filter.Validate();return filter;
    }
    private async void PickRoot(object sender,RoutedEventArgs e)
    {
        try{var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var folder=await picker.PickSingleFolderAsync();if(folder is not null){RootPath.Text=folder.Path;await OpenRoot(folder.Path);}}
        catch(Exception ex){ShowError(ex);}
    }
    private async void RootPathKeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;await OpenRoot(RootPath.Text);}}
    private async void RefreshRoot(object sender,RoutedEventArgs e)
    {
        await RefreshCurrentRoot();
    }
    private async Task RefreshCurrentRoot()
    {
        try
        {
            await OpenRoot(activeCollectionId is {} collection?"collection:"+collection:root,true,recordHistory:false,preserveDirectoryScope:true);
        }
        catch(OperationCanceledException){}
        catch(Exception error){ShowError(error);}
    }
    private long scanCancelRevision;
    private void CancelScan(object sender,RoutedEventArgs e)
    {
        scanCancelRevision++;activeBackgroundScan?.Cancel();scanStop.Cancel();Status.Text="正在停止扫描…";
        if(browserRootReady&&!replacingRoot)_=PublishStoppedScan(scanTask,scanStop,rootChangeVersion,directoryNavigationRequest,scanCancelRevision);
    }
    private async Task PublishStoppedScan(Task? task,CancellationTokenSource stopped,long version,long navigation,long revision)
    {
        using var operation=browserWork.Enter();if(operation is null)return;
        try
        {
            await StoppedScanPublication.Run(task,lifetime.Token,
                ()=>!closing&&!replacingRoot&&browserRootReady&&!BrowserSequenceLocked&&version==rootChangeVersion&&navigation==directoryNavigationRequest&&revision==scanCancelRevision&&ReferenceEquals(stopped,scanStop),
                ()=>RefreshQuery(preserveViewport:true));
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(version==rootChangeVersion&&!closing)ShowScanError(error);}
    }
    private async Task OpenRoot(string path,bool forceRefresh=false,bool recordHistory=true,SavedView? previousView=null,bool preserveDirectoryScope=false)
    {
        using var operation=browserWork.Enter();if(operation is null||closing||catalog is null)return;
        long navigation=++directoryNavigationRequest;
        var acceptedView=rootId.Length>0?CaptureView():null;
        var requestedAdvanced=advanced;
        try
        {
        if(!forceRefresh&&!preserveDirectoryScope&&!path.StartsWith("collection:",StringComparison.Ordinal))
        {
            path=PathRules.ValidateSource(path);
            if(await TryBrowseCurrentRoot(path,navigation))return;
            if(closing||navigation!=directoryNavigationRequest)return;
            var covering=backgroundScans.Where(s=>!s.Cancelled&&!s.Completion.IsCompleted&&s.Policy.HasSameScanPolicy(CurrentFilter())&&DirectoryBrowseScope.Relative(s.Path,path) is not null)
                .OrderByDescending(s=>s.Path.Length).FirstOrDefault();
            if(covering is not null&&!string.Equals(covering.Path,path,StringComparison.Ordinal))
            {
                previousView??=rootId.Length>0?CaptureView():null;
                requestedAdvanced=CurrentFilter() with{DirectoryScope=DirectoryBrowseScope.Relative(covering.Path,path)!,ScopeDirectFiles=false};
                path=covering.Path;preserveDirectoryScope=true;
            }
        }
        // Refuse a scan that cannot start before cancelling the current view or
        // clearing its rows. Existing scope queries remain available at the limit.
        if(!path.StartsWith("collection:",StringComparison.Ordinal))await catalog.EnsureBrowsingBudget(lifetime.Token);
        if(closing||navigation!=directoryNavigationRequest)return;
        }
        catch(OperationCanceledException){return;}
        catch(Exception error){if(navigation==directoryNavigationRequest)ShowScanError(error);return;}
        long requested=++rootChangeVersion;scanStop.Cancel();queryStop.Cancel();selectionStop.Cancel();
        bool acquired=false,accepted=false;
        try
        {
            bool collectionScope=path.StartsWith("collection:",StringComparison.Ordinal)&&Guid.TryParseExact(path[11..],"N",out _);
            if(!collectionScope)path=PathRules.ValidateSource(path);
            // Keep the accepted rows, snapshot and root until admission succeeds.
            // Another scan can exhaust the budget during the identity probe.
            replacingRoot=true;
            await rootChangeGate.WaitAsync(lifetime.Token);acquired=true;if(requested!=rootChangeVersion)return;
            await ReturnToBrowser();if(requested!=rootChangeVersion||closing)return;
            monitor?.Dispose();monitor=null;
            bool ownedScan=backgroundScans.Any(s=>ReferenceEquals(s.Completion,scanTask));
            await RootTaskRetirement.Wait(ownedScan?null:scanTask,metadataTask,scanStop.Token,lifetime.Token);scanTask=null;metadataTask=null;
            if(requested!=rootChangeVersion)return;
            scanStop.Dispose();scanStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            activeBackgroundScan=null;
            var reusable=collectionScope?null:backgroundScans.FirstOrDefault(s=>string.Equals(s.Path,path,StringComparison.Ordinal)&&!s.Completion.IsCompleted);
            if(reusable is not null&&(reusable.Cancelled||forceRefresh||!reusable.Policy.HasSameScanPolicy(CurrentFilter())))
            {
                reusable.Cancel();try{await reusable.Completion;}catch(OperationCanceledException){}reusable=null;
                if(requested!=rootChangeVersion||closing)return;
            }
            foreach(var finished in backgroundScans.Where(s=>s!=reusable&&s.Completion.IsCompleted).ToArray()){finished.Dispose();backgroundScans.Remove(finished);}
            if(collectionScope)
            {
                await catalog.RefreshPlaylist(path[11..],scanStop.Token);
                if(requested!=rootChangeVersion||closing)return;
                root=path;rootId=path;epoch=requested;
            }
            else
            {
                if(verifyRootAdmissionBarrier is not null)await verifyRootAdmissionBarrier(scanStop.Token);
                var opened=await new RootIdentityResolver(catalog,verifyScanWorkerExecutable??ScanWorkerClient.FindExecutable(ScanWorkerDirectory)).Open(path,scanStop.Token,reusable is null?null:(reusable.RootId,reusable.Epoch));
                if(reusable is not null&&(opened.RootId!=reusable.RootId||opened.Epoch!=reusable.Epoch))
                {reusable.Cancel();try{await reusable.Completion;}catch(OperationCanceledException){}reusable=null;}
                if(requested!=rootChangeVersion||closing)return;root=path;rootId=opened.RootId;epoch=opened.Epoch;activeBackgroundScan=reusable;
            }
            accepted=true;
            if(recordHistory&&acceptedView is not null&&!string.Equals(acceptedView.Root,path,StringComparison.Ordinal))navigationHistory.VisitFrom(previousView??acceptedView);
            advanced=requestedAdvanced;
            if(!preserveDirectoryScope&&acceptedView?.Root!=path&&advanced is not null)advanced=advanced with{DirectoryScope="",ScopeDirectFiles=false};
            DirectoryScopePanel.Visibility=Visibility.Collapsed;
            activeCollectionId=collectionScope?path[11..]:null;GroupingButton.IsEnabled=BrowseDepthButton.IsEnabled=!collectionScope;RootPath.IsReadOnly=collectionScope;
            if(!collectionScope&&advanced is not null)advanced=advanced with{CollectionId=null};
            RootPath.Text=collectionScope?"收藏夹："+CollectionLabel(activeCollectionId!):Path.Combine(path,advanced?.DirectoryScope??"");if(!collectionScope)ShowTreeRoot(path);else activeTreeRoot=null;UpdateNavigationButtons();
            browserScanError=null;browserEmptyError=null;generation++;queryBusy=false;ClearResultSelection();CancelThumbnails();results?.Dispose();results=null;
            firstPageSequence=[];firstPageFilter=null;firstPageRows.Clear();FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;if(viewerStrip is not null)viewerStrip.ItemsSource=null;ResultSummary.Text="正在读取文件夹当前内容…";
            browserRootReady=true;
            if(activeTreeRoot is {} treeRoot)treeRoot.Content=new FolderNode(path,(treeRoot.Content as FolderNode)?.Label??FolderLabel(path),rootId,"",path,Icon:(treeRoot.Content as FolderNode)?.Icon);
            QueueTreeRefresh();
            var oldHandle=resultHandle;resultHandle=null;
            if(oldHandle is not null)await catalog.ReleaseSnapshot(oldHandle.Id);
            if(requested!=rootChangeVersion||closing)return;
            prefetchStop.Cancel();ClearPrefetchedImages();prefetchedDetails.Clear();prefetchedDetailBytes=0;CancelThumbnails();selected=null;resultHandle=null;scanPreviewRefresh.Reset();results?.Dispose();FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;if(viewerStrip is not null)viewerStrip.ItemsSource=null;ClearImage();replacingRoot=false;
            if(collectionScope)
            {
                await RefreshQuery();
                if(requested!=rootChangeVersion||closing||scanStop.IsCancellationRequested)return;
                Status.Text="";
                _=StartMetadataRefresh();
                return;
            }
            string activeRoot=root,activeId=rootId;long activeEpoch=epoch;scanScheduler.Prefer(activeId);
            RootChangeMonitor CreateScanMonitor()=>new(activeRoot,()=>DispatcherQueue.TryEnqueue(()=>{if(activeId!=rootId||activeEpoch!=epoch||replacingRoot||closing)return;reconcilePending=true;_=Reconcile();}),catalog,activeId,activeEpoch,[dataDirectory]);
            var report=new Progress<ScanProgress>(p=>
            {
                scanProgress[activeId]=(p,DateTimeOffset.UtcNow);if(activeId!=rootId || activeEpoch!=epoch || replacingRoot || closing)return;
                Status.Text=$"已发现 {p.Files:N0} 个文件 · {p.Directories:N0} 个目录 · {p.Errors:N0} 个错误 · {p.State switch{"ready"=>"扫描完成","partial"=>"部分目录未完成","cancelled"=>"已取消",_=>"正在扫描"}}";
                if(Stopwatch.GetTimestamp()>=nextTreeRefresh){nextTreeRefresh=Stopwatch.GetTimestamp()+Stopwatch.Frequency;QueueTreeRefresh();_=RefreshCollectionsAfterScan();}
                if(scanPreviewRefresh.TryBegin(p.Files,resultHandle is not null,queryBusy,BrowserSequenceLocked,Stopwatch.GetElapsedTime(0)))
                {
                    _=RefreshQuery(preserveViewport:true,scanPreview:true);
                }
            });
            Status.Text="正在查找文件…";
            bool recursive=true;scannedPolicy=CurrentFilter();
            ExclusionSpec[] exclusions=ScanExclusions();
            string? scanWorker=verifyScanWorkerExecutable??ScanWorkerClient.FindExecutable(ScanWorkerDirectory);
            var rootToken=scanStop.Token;
            if(reusable is null)
            {
                var indexer=new DirectoryIndexer(catalog,scanWorker){Scheduler=scanScheduler};
                activeBackgroundScan=new(activeId,activeRoot,activeEpoch,scannedPolicy,indexer.Priority,scanScheduler,lifetime.Token,
                    // Re-enumeration does not invalidate unchanged media metadata.
                    token=>indexer.Scan(activeId,activeRoot,activeEpoch,recursive,exclusions,report,token),CreateScanMonitor);
                backgroundScans.Add(activeBackgroundScan);
                _=ObserveBackgroundCompletion(activeBackgroundScan);
            }
            scanTask=activeBackgroundScan!.Completion;
            var openedScanTask=activeBackgroundScan.Completion;
            if(reusable is not null&&CurrentFilter().DirectoryScope.Length>0)
            {
                await new ScanDirtyDirectories(catalog).Mark(activeId,activeEpoch,[new(CurrentFilter().DirectoryScope,"BrowseNavigation",true)],rootToken);
                if(requested!=rootChangeVersion||closing)return;
                reconcilePending=true;
            }
            PreferScanDirectory(activeId,CurrentFilter().DirectoryScope);
            UpdateBrowserEmptyState();
            rootChangeGate.Release();acquired=false;
            await RefreshQuery(preserveViewport:true,scanPreview:true);
            var completedScan=await openedScanTask;
            if(verifyScanBarrier is not null)await verifyScanBarrier(rootToken);
            if(requested!=rootChangeVersion||closing||rootToken.IsCancellationRequested)return;
            if(completedScan.State=="missing")
            {
                await RefreshQuery();
                if(requested==rootChangeVersion&&!closing&&!rootToken.IsCancellationRequested)ShowScanError(new DirectoryNotFoundException("文件夹已不存在，请选择其他文件夹。"));
                return;
            }
            if(completedScan.BudgetLimited)
            {
                await RefreshQuery(preserveViewport:true,scanPreview:true);
                if(requested==rootChangeVersion&&!closing)ShowScanError(new BrowsingBudgetException());
                return;
            }
            if(completedScan.State=="partial"&&completedScan.Files==0){ShowScanError(new IOException("无法读取此文件夹，请检查磁盘连接和访问权限后刷新。"));return;}
            if(activeId==rootId){QueueTreeRefresh();if(!BrowserSequenceLocked)await RefreshQuery(preserveViewport:true,scanPreview:true);else Status.Text+=" · 发现更多文件，点击“应用筛选”查看。";}
            if(settings is not null)await settings.Save("last-root.json",activeRoot);
            if(activeId==rootId&&!scanStop.IsCancellationRequested)_=StartMetadataRefresh();
            if(reconcilePending)_=Reconcile();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(requested==rootChangeVersion)ShowScanError(ex);}
        finally
        {
            if(acquired){if(requested==rootChangeVersion)replacingRoot=false;rootChangeGate.Release();}
            if(requested==rootChangeVersion)
            {
                replacingRoot=false;
                if(!accepted&&acceptedView is not null)
                {
                    RootPath.Text=activeCollectionId is null?Path.Combine(acceptedView.Root,acceptedView.Filter.DirectoryScope):"收藏夹："+CollectionLabel(activeCollectionId);
                    queryBusy=false;UpdateNavigationButtons();
                }
                UpdateBrowserEmptyState();
            }
            if(!closing)await RefreshCollectionsAfterScan();
        }
    }
    private async Task Reconcile(bool force=false)
    {
        using var operation=browserWork.Enter();if(operation is null)return;
        if(closing||replacingRoot||scanStop.IsCancellationRequested||catalog is null||scanTask is {IsCompleted:false}||!reconcilePending)return;reconcilePending=false;
        string activeRoot=root,activeId=rootId;long activeEpoch=epoch;scanScheduler.Prefer(activeId);bool recursive=true;var exclusions=ScanExclusions();
        var rootToken=scanStop.Token;long rootVersion=rootChangeVersion;
        try
        {
            if(force)Status.Text="正在刷新文件列表…";
            string? executable=verifyScanWorkerExecutable??ScanWorkerClient.FindExecutable(ScanWorkerDirectory);
            var task=Task.Run(async ()=>
            {
                var token=rootToken;if(verifyReconcileBarrier is not null)await verifyReconcileBarrier(token);
                return await (force?new DirectoryIndexer(catalog,executable){Scheduler=scanScheduler}.Scan(activeId,activeRoot,activeEpoch,recursive,exclusions,null,token):new DirectoryIndexer(catalog,executable){Scheduler=scanScheduler}.ReconcileDirty(activeId,activeRoot,activeEpoch,recursive,exclusions,null,token));
            });
            scanTask=task;var report=await task;
            if(rootVersion!=rootChangeVersion||activeId!=rootId||activeEpoch!=epoch||closing||rootToken.IsCancellationRequested)return;
            if(report.BudgetLimited){await RefreshQuery(preserveViewport:true,scanPreview:true);if(rootVersion==rootChangeVersion&&!closing)ShowScanError(new BrowsingBudgetException());return;}
            // A successful query or a dirty-subdirectory scan does not prove that a
            // previous root scan failure recovered. F5 performs the complete scope.
            if(force&&report.State=="ready")ClearScanError();
            if(report.State is "ready" or "missing" or "partial")
            {
                await RefreshQuery(preserveViewport:true,scanPreview:!force);
                if(rootVersion!=rootChangeVersion||closing||rootToken.IsCancellationRequested)return;
                if(report.State=="missing"){ShowScanError(new DirectoryNotFoundException("文件夹已不存在，请选择其他文件夹。"));return;}
            }
            _=StartMetadataRefresh();
            Status.Text=report.State=="ready"?"":"部分文件夹暂时无法读取，稍后会自动重试。";
        }
        catch(OperationCanceledException) when(rootToken.IsCancellationRequested){}
        catch(Exception ex){if(rootVersion==rootChangeVersion)ShowScanError(ex);}
        finally{if(!closing)await RefreshCollectionsAfterScan();if(reconcilePending&&!closing&&rootVersion==rootChangeVersion&&!rootToken.IsCancellationRequested)_=Reconcile();}
    }
    private async void ApplyFilters(object sender,RoutedEventArgs e){FilterFlyout?.Hide();searchTimer?.Stop();await ApplyBrowserFilters();}
    private Task ApplyBrowserFilters()=>activeCollectionId is not null?RefreshQuery():scannedPolicy is null||!scannedPolicy.HasSameScanPolicy(CurrentFilter())
        ?OpenRoot(root,true,recordHistory:false,preserveDirectoryScope:true):RefreshQuery();
    private async Task RefreshQuery(bool preserveViewport=false,bool scanPreview=false)
    {
        using var operation=browserWork.Enter();if(operation is null||closing||catalog is null||string.IsNullOrEmpty(rootId)||replacingRoot)return;
        if(!browserRootReady)return;
        string previousSummary=ResultSummary.Text;bool published=false,failed=false;string? candidateLease=null;string queryPhase="firstPage";
        if(scanPreview&&queryBusy){automaticQueryPending=true;await queryCompletion;return;}
        if(!scanPreview)automaticQueryPending=false;
        bool PreviewInterrupted()=>scanPreview&&BrowserSequenceLocked;
        if(PreviewInterrupted())return;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);queryCompletion=completion.Task;
        queryStop.Cancel();queryStop.Dispose();queryStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var queryToken=queryStop.Token;long gen=scanPreview?generation:++generation,request=++queryRequest;queryBusy=true;
        var attempt=new QueryAttempt(request,gen,epoch,rootId,scanPreview);
        bool IsCurrent()=>gen==generation&&request==queryRequest;
        browserEmptyError=null;UpdateBrowserEmptyState();
        try
        {
            FilterSpec filter=CurrentFilter();if(!scanPreview){metadataDemandStop.Cancel();metadataDemandStop.Dispose();metadataDemandStop=new();_=StartMetadataRefresh();}if(!scanPreview)PreferScanDirectory(rootId,filter.DirectoryScope);attempt=attempt with{FilterHash=QueryFilterHash(filter)};ShowActiveFilters(filter);if(!scanPreview)ResultSummary.Text="正在更新浏览结果…";string? previousPath=BrowserPath(selected);long restoreSelectionRequest=browserSelectionRequest;
            var activeList=DetailsMode.IsChecked==true?(ListViewBase)FilesList:FilesGrid;
            int firstVisible=activeList.ItemsPanelRoot switch{ItemsWrapGrid panel=>panel.FirstVisibleIndex,ItemsStackPanel panel=>panel.FirstVisibleIndex,_=>-1};
            string? viewportPath=preserveViewport&&firstVisible>=0&&firstVisible<activeList.Items.Count?BrowserPath(activeList.Items[firstVisible] as FileRow):null;
            if(resultHandle is null&&!filter.Grouping.Enabled&&!FirstPageMatches(filter))
            {
                var first=await catalog.ReadFirstPage(filter,queryToken);if(!IsCurrent()||closing||PreviewInterrupted())return;
                // A root can contain only non-images while its descendants are
                // still queued. Do not freeze that temporary empty result.
                if(ScanPreviewRefresh.DeferFirstBatch(first.Items.Count,scanTask is {IsCompleted:false}&&!scanStop.IsCancellationRequested))
                {
                    ResultSummary.Text="正在加载首批文件…";
                    return;
                }
                PublishFirstPage(filter,first.Items);
                ResultSummary.Text=$"已显示前 {first.Items.Count:N0} 个文件，正在整理完整列表…";
                if(verifyFirstPageBarrier is not null)await verifyFirstPageBarrier(queryToken);
            }
            queryPhase="createSnapshot";var handle=await catalog.CreateSnapshot(filter,epoch,gen,queryToken);
            if(!IsCurrent() || closing){await catalog.ReleaseSnapshot(handle.Id);return;}
            // The most recently built candidate is not necessarily the one displayed.
            // Hold a real lease until replacement/close, including across discarded builds.
            if(!await catalog.RetainSnapshot(handle.Id))throw new InvalidOperationException("文件列表已变化，请刷新后重试。");
            candidateLease=handle.Id;
            if(verifyCandidateBarrier is not null)await verifyCandidateBarrier(queryToken);
            if(!IsCurrent()||closing||PreviewInterrupted())return;
            queryPhase="readSnapshot";IReadOnlyList<SnapshotGroup> groups=filter.Grouping.Enabled?await catalog.ReadGroups(handle.Id,queryToken):Array.Empty<SnapshotGroup>();
            if(!IsCurrent()||closing||PreviewInterrupted())return;
            if(preserveViewport)viewportPath=VisibleBrowserPath()??viewportPath;
            var previousHandle=resultHandle;var previousResults=results;
            bool promoting=previousHandle is null&&FirstPageMatches(filter)&&groups.Count==0;
            bool incremental=scanPreview&&previousHandle is not null&&previousResults is not null&&((browserGroups is not null)==(groups.Count>0));
            IReadOnlyDictionary<string,SnapshotSplice>? edits=null;IReadOnlyList<SnapshotItem> matching=Array.Empty<SnapshotItem>();
            IReadOnlyDictionary<string,IReadOnlyList<RangeEdit>?>? refined=null;
            var previousGroups=browserGroups?.Select(group=>group.Info).ToArray()??Array.Empty<SnapshotGroup>();
            if(incremental)
            {
                edits=await catalog.CompareSnapshots(previousHandle!,handle,previousGroups,groups,queryToken);
                refined=await catalog.RefineSnapshotChanges(previousHandle!,handle,previousGroups,groups,edits,queryToken);
                var ids=(selected is {} selectedRow?new[]{selectedRow}:Array.Empty<FileRow>()).Concat(visible).Concat(previousResults!.CachedRows()).Where(row=>row.Item is not null).Select(row=>row.Item!.EntryId).Distinct().Take(8192).ToArray();
                matching=await catalog.ReadSnapshotEntries(handle.Id,ids,queryToken);
                if(!IsCurrent()||closing||PreviewInterrupted())return;
                previousPath=BrowserPath(selected);restoreSelectionRequest=browserSelectionRequest;
            }
            else if(promoting)
            {
                matching=await catalog.ReadSnapshotEntries(handle.Id,firstPageSequence.Select(row=>row.Item!.EntryId).ToArray(),queryToken);
                if(!IsCurrent()||closing)return;
                previousPath=BrowserPath(selected);restoreSelectionRequest=browserSelectionRequest;
            }
            var nextResults=new VirtualResults(catalog,handle,DispatcherQueue);nextResults.SetPresentation(GridCardWidth,gridShowPaths);
            bool adopted=false;
            activeList=ActiveBrowser;
            var publicationAnchor=incremental||promoting?CapturePublicationViewport(activeList):null;
            if(incremental||promoting)publicationRows=visible.ToHashSet();
            queryPhase="publish";updatingBrowser=true;
            long publicationStart=Stopwatch.GetTimestamp();
            Dictionary<string,double>? publicationStages=verifyPublicationStages is null?null:[];
            long stageStart=publicationStart;
            void Stage(string name){if(publicationStages is null)return;publicationStages[name]=Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;stageStart=Stopwatch.GetTimestamp();}
            try
            {
                if(incremental)
                {
                    var rows=(selected is {} selectedRow?new[]{selectedRow}:Array.Empty<FileRow>()).Concat(visible).Concat(previousResults!.CachedRows().Take(4096)).Where(row=>previousResults.IndexOf(row)>=0).DistinctBy(row=>previousResults.IndexOf(row)).ToArray();
                    var stableChanges=PreserveVisibleRanges(rows,previousGroups,groups,edits!,matching)
                        .ToDictionary(pair=>pair.Key,pair=>refined![pair.Key]??pair.Value);
                    Stage("ranges");
                    RetainBrowserRows(previousResults,nextResults,rows,previousGroups,groups,edits!,matching);
                    Stage("retain");
                    var sharedRanges=VirtualResults.SharedRanges(previousResults.Count,nextResults.Count,previousGroups,groups,stableChanges);
                    Stage("sharedRanges");
                    long identityAllocated=publicationStages is null?0:GC.GetAllocatedBytesForCurrentThread();
                    int identityGc0=publicationStages is null?0:GC.CollectionCount(0),identityGc1=publicationStages is null?0:GC.CollectionCount(1),identityGc2=publicationStages is null?0:GC.CollectionCount(2);
                    using var identities=previousResults.ShareUnchangedRowsWith(nextResults,sharedRanges);
                    Stage("identities");
                    if(publicationStages is not null)
                    {
                        publicationStages["identityAllocatedBytes"]=GC.GetAllocatedBytesForCurrentThread()-identityAllocated;
                        publicationStages["identityGc0"]=GC.CollectionCount(0)-identityGc0;publicationStages["identityGc1"]=GC.CollectionCount(1)-identityGc1;publicationStages["identityGc2"]=GC.CollectionCount(2)-identityGc2;
                        publicationStages["identityLiveRows"]=previousResults.CachedRows().Count();publicationStages["sharedRangeCount"]=sharedRanges.Count;
                        Stage("diagnostics");
                    }
                    CancelObsoleteThumbnails(nextResults);resultHandle=handle;results=nextResults;candidateLease=null;adopted=true;
                    Stage("cancel");
                    long notificationAllocated=publicationStages is null?0:GC.GetAllocatedBytesForCurrentThread();
                    int notificationGc0=publicationStages is null?0:GC.CollectionCount(0),notificationGc1=publicationStages is null?0:GC.CollectionCount(1),notificationGc2=publicationStages is null?0:GC.CollectionCount(2);
                    UpdateBrowserResults(nextResults,groups,stableChanges,publicationStages);
                    RestorePublicationViewport(activeList,publicationAnchor);
                    Stage("notifications");
                    if(publicationStages is not null)
                    {
                        publicationStages["notificationAllocatedBytes"]=GC.GetAllocatedBytesForCurrentThread()-notificationAllocated;
                        publicationStages["notificationGc0"]=GC.CollectionCount(0)-notificationGc0;publicationStages["notificationGc1"]=GC.CollectionCount(1)-notificationGc1;publicationStages["notificationGc2"]=GC.CollectionCount(2)-notificationGc2;
                        publicationStages["notificationAdded"]=stableChanges.Values.Sum(edits=>edits.Sum(edit=>(long)edit.Added));
                        publicationStages["notificationRemoved"]=stableChanges.Values.Sum(edits=>edits.Sum(edit=>(long)edit.Removed));
                        publicationStages["notificationLiveRows"]=nextResults.CachedRows().Count();
                        Stage("notificationDiagnostics");
                    }
                    foreach(var item in visibleContainers.ToArray())if(nextResults.IndexOf(item.Value)>=0)_=LoadThumbnail(item.Key.View,item.Value);
                    Stage("visible");
                }
                else if(promoting)
                {
                    var changes=PromoteFirstPage(nextResults,matching);
                    CancelObsoleteThumbnails(nextResults);resultHandle=handle;results=nextResults;candidateLease=null;adopted=true;
                    UpdatePromotedFirstPage(nextResults,changes);RestorePublicationViewport(activeList,publicationAnchor);
                    foreach(var item in visibleContainers.ToArray())if(nextResults.IndexOf(item.Value)>=0)_=LoadThumbnail(item.Key.View,item.Value);
                }
                else{ClearResultSelection();CancelThumbnails();resultHandle=handle;results=nextResults;candidateLease=null;adopted=true;BindBrowserResults(results,groups);}
                if(viewerStrip is not null)viewerStrip.ItemsSource=results;
                verifyPublishFault?.Invoke();
                lastAppliedFilter=filter;
            }
            catch(Exception publicationError)
            {
                verifyPublicationFailure?.Invoke(publicationError);
                if(adopted)
                {
                    // A failed notification may leave an intermediate collection exposed.
                    // Finish binding the owned new source before retiring the old source.
                    try{ClearResultSelection();CancelThumbnails();BindBrowserResults(nextResults,groups);if(viewerStrip is not null)viewerStrip.ItemsSource=nextResults;lastAppliedFilter=filter;}
                    catch{FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;if(viewerStrip is not null)viewerStrip.ItemsSource=null;results=null;resultHandle=null;candidateLease=handle.Id;nextResults.Dispose();throw;}
                }
                throw;
            }
            finally
            {
                verifyPublicationDuration?.Invoke(Stopwatch.GetElapsedTime(publicationStart).TotalMilliseconds);
                if(publicationStages is not null)verifyPublicationStages?.Invoke(publicationStages);
                updatingBrowser=false;
                ReleasePublicationRows();
                UpdateBrowserEmptyState();
                if(adopted){previousResults?.Dispose();if(previousHandle is not null)await catalog.ReleaseSnapshot(previousHandle.Id);}
                else nextResults.Dispose();
                if(verifyRetirementBarrier is not null)await verifyRetirementBarrier();
            }
            published=true;
            if(!IsCurrent()||closing||!ReferenceEquals(results,nextResults)||!ReferenceEquals(resultHandle,handle))return;
            UpdateDirectoryScopeBanner(filter);
            ResultSummary.Text=UserMessages.Results(handle.Count,handle.Pending,handle.Unresolvable,handle.IsPendingView)+(handle.Count==0?(scanTask is {IsCompleted:false}?" · 正在查找更多文件":" · 当前没有可显示的文件"):"");
            if(verifySelectionRestoreBarrier is not null)await verifySelectionRestoreBarrier();
            if(previousPath is not null)
            {
                var ordinal=await catalog.FindOrdinal(handle.Id,previousPath,queryToken);
                if(IsCurrent()&&!closing&&restoreSelectionRequest==browserSelectionRequest)
                {
                    if(ordinal is {} position){var restored=(FileRow)results[(int)position]!;if(!incremental&&!promoting)RevealBrowserRow(restored);ActiveBrowser.SelectedItem=restored;}
                    else{ActiveBrowser.SelectedItem=null;ClearResultSelection();}
                }
            }
            // Promotion already restored the row's exact pixel offset above.
            // A second leading-edge scroll would discard that offset.
            else if(!incremental&&!promoting&&!string.IsNullOrEmpty(viewportPath)&&await catalog.FindOrdinal(handle.Id,viewportPath,queryToken) is {} anchor&&IsCurrent())
                activeList.ScrollIntoView(results[(int)anchor],ScrollIntoViewAlignment.Leading);
            if(IsCurrent()&&!closing)await TryRestoreBrowserView();
        }
        catch(OperationCanceledException ex){await RecordQueryFailure(ex,queryPhase,attempt,candidateLease);}
        catch(Exception ex){failed=true;await RecordQueryFailure(ex,queryPhase,attempt,candidateLease);if(IsCurrent()&&!closing)ShowBrowserError(ex);}
        finally
        {
            try
            {
                if(candidateLease is not null)await catalog.ReleaseSnapshot(candidateLease);
                if(IsCurrent()){queryBusy=false;if(scanPreview){scanPreviewRefresh.Complete(Stopwatch.GetElapsedTime(0));if(!published&&!failed&&resultHandle is not null)ResultSummary.Text=previousSummary;}UpdateBrowserEmptyState();
                    if(automaticQueryPending&&!closing){automaticQueryPending=false;await RefreshQuery(scanPreview:true);}}
            }
            finally{completion.TrySetResult();}
        }
    }
    private async void SelectFile(object sender,SelectionChangedEventArgs e)
    {
        if(closing||updatingBrowser||sender is not ListViewBase view||SelectionPreview(view) is not FileRow row||ReferenceEquals(selected,row))return;
        await SelectPreview(row);
    }
    private async Task SelectPreview(FileRow row)
    {
        verifyPreviewStage?.Invoke("selection");
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        browserSelectionRequest++;
        ResetViewerGesture();
        rawPreviewOnly=false;
        AnimationButton.Visibility=ReplayAnimationButton.Visibility=Visibility.Collapsed;
        pendingAnimationBitmap?.Dispose();pendingAnimationBitmap=null;animationFrameIndex=animationNextFrame=0;animationNeedsOpen=true;animationDeadline=0;animationCompletedLoops=0;animationCompleted=false;animationRevision++;
        if(selected is {} previous&&previous.Ordinal!=row.Ordinal)prefetchDirection=row.Ordinal>previous.Ordinal?1:-1;
        if(!CanAdoptPrefetch(row))prefetchStop.Cancel();slideTimer?.Stop();animationTimer?.Stop();animationRunning=false;StopVideo();StopAudio();AudioState.Text="";AudioTools.Visibility=Visibility.Collapsed;FrameTools.Visibility=Visibility.Collapsed;imagePage=0;imagePageCount=1;
        MarkdownHost.Visibility=Visibility.Collapsed;markdownImages.Clear();
        selected=row;requestedImagePage=null;selectionStop.Cancel();selectionStop.Dispose();selectionStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=selectionStop.Token;long current=++selection;PreparePreview();FileTitle.Text=row.Name;QualityLabel.Text="正在读取…";
        UpdateViewerInformation();BeginPreviewDiagnostics(current);
        try
        {
            if(verifyPreviewBarrier is not null)await verifyPreviewBarrier(token);
            if(animationIdle is {} retiringAnimation)await retiringAnimation.Task.WaitAsync(token);
            var rowSource=results;await ResetTextSession();verifyPreviewStage?.Invoke("resetText");if(row.Item is null)await (rowSource??throw new InvalidOperationException("当前结果已关闭。")).EnsureLoaded(row,token);if(current!=selection)return;
            UpdateViewerInformation();
            RecordPreviewStage(current,"properties");
            var properties=await ResolveRow(row,rootId,token);if(current!=selection)return;selectedProperties=properties;FileTitle.Text=row.Name;offlinePreview=false;
            verifyPreviewStage?.Invoke("properties");
            bool cloud=selectedProperties.HydrationState=="placeholder"&&!approvedCloud.Contains(CloudKey(row));cloudPreviewButton!.Visibility=cloud?Visibility.Visible:Visibility.Collapsed;
            if(cloud){QualityLabel.Text="此文件仅在线，读取前需要确认。";ClearImage();return;}
            await RenderSelectedContent(row,current,token);
            UpdateViewerInformation();
            if(slideShow&&current==selection&&row.Kind=="image")slideTimer?.Start();
        }
        catch(OperationCanceledException){RecordPreviewStage(current,"cancelled");}
        catch(Exception ex){if(current==selection&&!closing){ClearImage();ShowPreviewError(ex);}}
        finally{if(current==selection&&!closing){FinishPreview();if(previewStage is not ("failed" or "cancelled"))RecordPreviewStage(current,"finished");verifyPreviewStage?.Invoke("finished");}}
        if(current==selection&&!closing&&!offlinePreview&&row.Kind=="image")await LoadSelectedExif(row,current,token);
    }
    private async Task RenderSelectedContent(FileRow row,long current,CancellationToken token)
    {
        string path=SourcePath(row),kind=row.Kind;
        var bookmark=TakePreviewBookmark(row);
        TextScroll.Visibility=kind is "text" or "markdown"?Visibility.Visible:Visibility.Collapsed;TextTools.Visibility=TextScroll.Visibility;UpdateReaderControls();ImageCanvas.Visibility=kind=="image"?Visibility.Visible:Visibility.Collapsed;
        if(kind=="image")
        {
            try{await LoadImage(row,current,token);}catch(Exception ex) when(IsSourceUnavailable(ex)){if(!await ShowOfflineThumbnail(row,current,token))throw;}
            if(current==selection&&!token.IsCancellationRequested&&bookmark is {ImagePage:>0}&&!offlinePreview)await ImagePage(bookmark.ImagePage-imagePage);
        }
        else if(kind is "text" or "markdown")
        {
            RecordPreviewStage(current,"textRead");
            if(bookmark is not null)
            {
                restoringTextEncoding=true;
                try{textEncoding=bookmark.Encoding;SelectTag(TextEncoding,textEncoding??"auto");}
                finally{restoringTextEncoding=false;}
            }
            await LoadText(bookmark?.TextOffset??0,current,token);if(current!=selection||token.IsCancellationRequested)return;
            await StartTextIndex(current,token);
            if(kind=="markdown"&&row.Item!.Bytes<=8*1024*1024&&(bookmark is null||bookmark.RenderedMarkdown))await LoadMarkdown(current,token);
            if(bookmark is not null&&current==selection&&TextScroll.Visibility==Visibility.Visible){TextScroll.UpdateLayout();TextScroll.ChangeView(null,bookmark.TextScroll,null,true);}
        }
        else if(kind is "video" or "audio")await LoadMedia(path,kind,current,token);
        else QualityLabel.Text=$"{row.Detail} · {row.RelativePath}";
        if(current==selection&&!token.IsCancellationRequested)previewReadySelection=current;
    }
    private RequestContext Context(FileRow row,long current)=>new(rootId,epoch,generation,current,row.Item!.Version,1,resultHandle?.Id);
    private async Task LoadImage(FileRow row,long current,CancellationToken cancellation)
    {
        string path=SourcePath(row);int width=Math.Max(256,(int)(ImageCanvas.ActualWidth*Shell.XamlRoot.RasterizationScale)),height=Math.Max(256,(int)(ImageCanvas.ActualHeight*Shell.XamlRoot.RasterizationScale));
        RecordPreviewStage(current,"prefetch");
        bool raw=FileKinds.Raw.Contains(Path.GetExtension(path));var cached=await FindPrefetched(row,width,height,cancellation);WorkerEnvelope? message=null;
        verifyPreviewStage?.Invoke(cached is null?"prefetchMiss":"prefetchHit");
        if(current!=selection||cancellation.IsCancellationRequested)return;
        if(cached is not null){await PresentPrefetched(cached,current,cancellation);if(current!=selection||cancellation.IsCancellationRequested)return;message=cached.Message;}
        if(cached is null)
        {
            RecordPreviewStage(current,"decode");
            var reply=raw
                ?await RawPreview.Open(operation=>previewWorker!.Request(path,operation,Context(row,current),new(width,height),cancellation,Stamp(row)))
                :await previewWorker!.Request(path,"fit",Context(row,current),new(width,height),cancellation,Stamp(row));
            message=reply.Message;
            verifyPreviewStage?.Invoke("workerFit");
            // Display first. Metadata writes do not gate the loading indicator.
            await PresentFit(reply,current,cancellation);
            if(current!=selection||cancellation.IsCancellationRequested)return;
            verifyPreviewStage?.Invoke("presented");
            _=RecordPreviewMetadata(row,reply,current,cancellation);
        }
        if(current==selection){await EnsureFitResolution(row,current,cancellation);QualityLabel.Text=$"{sourceWidth:N0} × {sourceHeight:N0} · {(rawPreviewOnly?"相机预览":message!.Quality=="rawDeveloped"?"RAW 原图 · 适应窗口":"清晰适应屏幕")}";}
        if(current==selection)
        {
            var metadata=message!.Metadata!.Value;bool animated=metadata.GetProperty("isAnimated").GetBoolean();imagePageCount=animated?1:metadata.GetProperty("pages").GetInt32();FrameTools.Visibility=animated||imagePageCount>1?Visibility.Visible:Visibility.Collapsed;AnimationButton.Visibility=ReplayAnimationButton.Visibility=animated?Visibility.Visible:Visibility.Collapsed;PreviousPageButton.Visibility=NextPageButton.Visibility=ImagePageLabel.Visibility=animated?Visibility.Collapsed:Visibility.Visible;ImagePageLabel.Text=$"1 / {imagePageCount}";
            if(animated){animationRunning=true;await AdvanceAnimation(true);}
            SchedulePrefetch(current);
        }
    }
    private static bool CanRetainRawPreview(Exception error)=>MediaPreviewFallback.CanRetainRawPreview(error);
    private async Task RecordPreviewMetadata(FileRow row,ImageReply reply,long current,CancellationToken token)
    {
        using var work=browserWork.Enter();if(work is null||closing)return;
        try
        {
            await RecordMetadata(row,reply,token);
            if(current==selection&&row.Kind!="image")
            {ClearImage();ImageCanvas.Visibility=Visibility.Collapsed;QualityLabel.Text=$"{row.Detail} · 请用其他应用打开此文件。";}
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(current==selection&&!closing)ShowError(error);}
    }
    private async Task EnsureEmbeddedRawResolution(FileRow row,long current,CancellationToken token,bool nativeSize=false)
    {
        if(!rawPreviewOnly||fitBitmap is null)return;
        double scale=nativeSize||zoom>0?1:EffectiveScale()*Shell.XamlRoot.RasterizationScale;
        int width=Math.Clamp((int)Math.Ceiling(sourceWidth*Math.Min(1,scale)),1,16384);
        int height=Math.Clamp((int)Math.Ceiling(sourceHeight*Math.Min(1,scale)),1,16384);
        if(fitBitmap.SizeInPixels.Width+1>=width&&fitBitmap.SizeInPixels.Height+1>=height)return;
        var reply=await previewWorker!.Request(SourcePath(row),"rawEmbedded",Context(row,current),new(width,height),token,Stamp(row));
        await PresentFit(reply,current,token);
    }
    private async Task AdvanceAnimation(bool open)
    {
        if(!animationRunning||selected is null||closing)return;long current=selection,revision=animationRevision;var token=selectionStop.Token;
        open|=animationNeedsOpen;
        if(animationLoading){if(open){animationTimer!.Interval=TimeSpan.FromMilliseconds(25);animationTimer.Start();}return;}
        animationLoading=true;var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);animationIdle=completed;
        try
        {
            CanvasBitmap frame;JsonElement data;
            if(pendingAnimationBitmap is {} pending){frame=pending;pendingAnimationBitmap=null;data=pendingAnimationData;}
            else
            {
                ImageReply reply;
                try{reply=await previewWorker!.Request(SourcePath(selected),open?"animationOpen":"animationFrame",Context(selected,current),open?new(1280,1280,FrameIndex:animationNextFrame,CompletedLoops:animationCompletedLoops):new(1280,1280),token,Stamp(selected));}
                catch(InvalidDataException ex) when(!open&&ex.Message=="AnimationSessionLost"&&current==selection&&revision==animationRevision)
                {reply=await previewWorker!.Request(SourcePath(selected),"animationOpen",Context(selected,current),new(1280,1280,FrameIndex:animationNextFrame,CompletedLoops:animationCompletedLoops),token,Stamp(selected));animationDeadline=0;}
                frame=await LoadRenderedBitmap(reply);data=reply.Message.Metadata!.Value;
            }
            if(current!=selection||revision!=animationRevision||token.IsCancellationRequested){frame.Dispose();return;}
            animationNeedsOpen=false;
            if(!animationRunning){pendingAnimationBitmap?.Dispose();pendingAnimationBitmap=frame;pendingAnimationData=data;return;}
            fitBitmap?.Dispose();fitBitmap=frame;animationFrameIndex=data.GetProperty("frameIndex").GetInt32();
            int frameCount=data.GetProperty("frameCount").GetInt32();long totalPlays=data.GetProperty("totalPlays").GetInt64();animationCompletedLoops=data.GetProperty("completedLoops").GetInt64();animationCompleted=data.GetProperty("completed").GetBoolean();animationNextFrame=(animationFrameIndex+1)%frameCount;
            foreach(var tile in tiles.Values)tile.Dispose();tiles.Clear();ImageCanvas.Invalidate();
            QualityLabel.Text=$"动图 · {animationFrameIndex+1} / {frameCount} 帧 · {(totalPlays==0?"无限循环":$"共 {totalPlays} 次播放")}";AnimationButton.Content="暂停动图";
            if(animationCompleted){animationRunning=false;AnimationButton.Content="重播动图";QualityLabel.Text+=" · 播放完毕";return;}
            long now=Stopwatch.GetTimestamp();if(open||animationDeadline==0)animationDeadline=now;
            animationDeadline+=checked((long)(data.GetProperty("durationMs").GetInt32()*(double)Stopwatch.Frequency/1000));
            animationTimer!.Interval=TimeSpan.FromSeconds(Math.Max(.001,(animationDeadline-now)/(double)Stopwatch.Frequency));animationTimer.Start();
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection&&revision==animationRevision){animationRunning=false;AnimationButton.Content="播放动图";ShowPreviewError(ex);}}
        finally{animationLoading=false;completed.TrySetResult();}
    }
    private void PauseAnimationForDetail()
    {if(AnimationButton.Visibility!=Visibility.Visible)return;animationRunning=false;animationTimer?.Stop();animationDeadline=0;AnimationButton.Content="播放动图";}
    private async void ToggleAnimation(object sender,RoutedEventArgs e)
    {
        if(animationRunning){PauseAnimationForDetail();return;}
        if(animationCompleted){RestartAnimation();return;}
        ResetViewerGesture();animationRunning=true;animationDeadline=0;foreach(var tile in tiles.Values)tile.Dispose();tiles.Clear();await AdvanceAnimation(false);
    }
    private void RestartAnimation()
    {
        ResetViewerGesture();animationTimer?.Stop();pendingAnimationBitmap?.Dispose();pendingAnimationBitmap=null;animationRevision++;animationNextFrame=0;animationCompletedLoops=0;animationCompleted=false;animationNeedsOpen=true;animationDeadline=0;animationRunning=true;_=AdvanceAnimation(true);
    }
    private void ReplayAnimation(object sender,RoutedEventArgs e)=>RestartAnimation();
    private async Task<long?> ImagePage(int delta)
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return null;
        if(selected is null||imagePageCount<=1)return null;
        int basis=requestedImagePage??imagePage,page=Math.Clamp(basis+delta,0,imagePageCount-1);if(page==basis)return null;
        requestedImagePage=page;var row=selected;ResetViewerGesture();prefetchStop.Cancel();
        selectionStop.Cancel();selectionStop.Dispose();selectionStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=selectionStop.Token;long current=++selection;
        viewerSizedSelection=current;
        PreparePreview();
        try
        {
            if(verifyPageBarrier is not null)await verifyPageBarrier(token);
            var reply=await previewWorker!.Request(SourcePath(row),"page",Context(row,current),new(1600,1200,PageIndex:page),token,Stamp(row));
            await PresentFit(reply,current,token);if(current!=selection||token.IsCancellationRequested)return null;
            imagePage=page;ImagePageLabel.Text=$"{page+1} / {imagePageCount}";previewReadySelection=current;FinishPreview();
            if(zoom>0)await LoadVisibleTiles();
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection)ShowPreviewError(ex);}
        finally{if(current==selection){requestedImagePage=null;FinishPreview();}}
        return current==selection&&!token.IsCancellationRequested&&imagePage==page?current:null;
    }
    private async void PreviousImagePage(object sender,RoutedEventArgs e)=>await ImagePage(-1);
    private async void NextImagePage(object sender,RoutedEventArgs e)=>await ImagePage(1);
    private async Task LoadMedia(string path,string kind,long current,CancellationToken token)
    {
        using var request=CancellationTokenSource.CreateLinkedTokenSource(token);
        mediaCoverStop=request;token=request.Token;
        try
        {
        if(verifyMediaCoverBarrier is not null)await verifyMediaCoverBarrier(token);
        token.ThrowIfCancellationRequested();
        long resources=imageResourceRevision;
        AudioTools.Visibility=kind=="audio"?Visibility.Visible:Visibility.Collapsed;var info=await Task.Run(()=>media!.Probe(path,token,WorkerPriority.Foreground),token);if(current!=selection||token.IsCancellationRequested)return;
        QualityLabel.Text=$"{info.VideoCodec??info.AudioCodec??"编码未知"} · {(info.DurationMs is {} ms?TimeSpan.FromMilliseconds(ms).ToString():"时长未知")}";
        if(kind=="video" || info.HasCover)
        {
            string folder=Path.Combine(RuntimeDataDirectory,"temp","covers");Directory.CreateDirectory(folder);string output=Path.Combine(folder,Guid.NewGuid().ToString("N")+".png");
            int edge=kind=="video"?1024:512;
            try
            {
                await media!.Cover(path,output,info,token,edge,WorkerPriority.Foreground);
                var bitmap=await LoadLocalBitmap(output);
                if(current!=selection||token.IsCancellationRequested||resources!=imageResourceRevision){bitmap.Dispose();return;}
                fitBitmap?.Dispose();fitBitmap=bitmap;
                sourceWidth=bitmap.SizeInPixels.Width;sourceHeight=bitmap.SizeInPixels.Height;
                zoom=0;pan=Vector2.Zero;rotation=0;
                if(kind=="video")QualityLabel.Text="视频封面 · "+QualityLabel.Text;
                ImageCanvas.Visibility=Visibility.Visible;ImageCanvas.Invalidate();UpdateViewerCursor();
            }
            finally{if(File.Exists(output))File.Delete(output);}
        }
        }
        catch(Exception) when(request.IsCancellationRequested)
        {
            // Playing or switching superseded this cover request. Its result
            // (including decoder failure) no longer owns the preview surface.
            RecordPreviewStage(current,"coverCancelled");
        }
        finally{if(ReferenceEquals(mediaCoverStop,request))mediaCoverStop=null;}
    }
    private async Task<CanvasBitmap> LoadRenderedBitmap(ImageReply reply)
    {
        CanvasBitmap? bitmap=null;
        try
        {
            // This is the worker-owned local output, never the original source.
            // Close the native stream before releasing the worker asset. The path
            // overload retains a file lock; the managed adapter adds measured cost.
            verifyPreviewStage?.Invoke("bitmapLoadStart");
            bitmap=await LoadLocalBitmap(reply.AssetPath!);verifyPreviewStage?.Invoke("bitmapLoadDone");
            return bitmap;
        }
        finally
        {
            try{if(previewWorker is not null)await previewWorker.ReleaseAsset(reply);verifyPreviewStage?.Invoke("assetReleased");}
            catch{bitmap?.Dispose();throw;}
        }
    }
    private async Task<CanvasBitmap> LoadLocalBitmap(string path)
    {
        using var stream=await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path,Windows.Storage.FileAccessMode.Read);
        return await CanvasBitmap.LoadAsync(ImageCanvas,stream);
    }
    private async Task PresentFit(ImageReply reply,long current,CancellationToken cancellation)
    {
        if(current!=selection||cancellation.IsCancellationRequested){await previewWorker!.ReleaseAsset(reply);cancellation.ThrowIfCancellationRequested();return;}
        RecordPreviewStage(current,"bitmap");
        var bitmap=await LoadRenderedBitmap(reply);if(current!=selection || cancellation.IsCancellationRequested){bitmap.Dispose();return;}
        fitBitmap?.Dispose();fitBitmap=bitmap;verifyBitmapSelection=current;sourceWidth=reply.Message.Metadata!.Value.GetProperty("width").GetInt32();sourceHeight=reply.Message.Metadata.Value.GetProperty("height").GetInt32();rawPreviewOnly=reply.Message.Quality=="rawEmbedded";ImageCanvas.Opacity=1;ApplyViewerSizing();
        RecordPreviewStage(current,"presented");
    }
    private void DrawImage(CanvasControl sender,CanvasDrawEventArgs args)
    {
        args.DrawingSession.Clear(Microsoft.UI.ColorHelper.FromArgb(255,32,36,43));if(fitBitmap is null || sourceWidth<=0)return;
        double scale=EffectiveScale();Vector2 origin=new((float)((sender.ActualWidth-sourceWidth*scale)/2)+pan.X,(float)((sender.ActualHeight-sourceHeight*scale)/2)+pan.Y);
        var center=new Vector2((float)sender.ActualWidth/2,(float)sender.ActualHeight/2);args.DrawingSession.Transform=Matrix3x2.CreateRotation(rotation*(float)Math.PI/2,center);
        args.DrawingSession.DrawImage(fitBitmap,new Rect(origin.X,origin.Y,sourceWidth*scale,sourceHeight*scale));
        foreach(var pair in tiles){var size=pair.Value.SizeInPixels;args.DrawingSession.DrawImage(pair.Value,new Rect(origin.X+pair.Key.Item1*1024*scale,origin.Y+pair.Key.Item2*1024*scale,size.Width*scale,size.Height*scale));}
        args.DrawingSession.Transform=Matrix3x2.Identity;
        RecordPreviewDraw();
        if(verifyBitmapSelection==selection)verifyImageDrawn?.Invoke(selection);
    }
    private double EffectiveScale()
    {
        if(zoom>0)return zoom;
        double fit=Math.Min(ImageCanvas.ActualWidth/(rotation%2==0?sourceWidth:sourceHeight),ImageCanvas.ActualHeight/(rotation%2==0?sourceHeight:sourceWidth));
        return viewerScaleIntent==ViewerScaleIntent.Default?Math.Min(1/(Shell.XamlRoot?.RasterizationScale??1),fit):fit;
    }
    private async Task LoadVisibleTiles()
    {
        if(selected?.Item is null || selected.Kind!="image"||sourceWidth<=0 || zoom<=0||previewLoading||offlinePreview)return;long current=selection;var token=selectionStop.Token;
        bool animated=AnimationButton.Visibility==Visibility.Visible;if(animated)PauseAnimationForDetail();int frameIndex=animationFrameIndex;
        if(!await tileGate.WaitAsync(0)){tileReloadPending=true;return;}tileReloadPending=false;
        try
        {
            if(rawPreviewOnly){await EnsureEmbeddedRawResolution(selected,current,token);if(current==selection)QualityLabel.Text=$"相机预览 · {zoom*Shell.XamlRoot.RasterizationScale:P0} · {sourceWidth:N0} × {sourceHeight:N0}";return;}
            if(current!=selection||token.IsCancellationRequested)return;
            tileReloadPending=false; // The range below includes any viewport changes during RAW development.
            var range=ImageViewport.VisibleTiles(sourceWidth,sourceHeight,ImageCanvas.ActualWidth,ImageCanvas.ActualHeight,EffectiveScale(),pan.X,pan.Y,rotation);
            int firstX=range.FirstX,firstY=range.FirstY,lastX=range.LastX,lastY=range.LastY;
            if(range.Count>128){QualityLabel.Text="当前缩放使用受限预览；放大后读取原始像素。";return;}
            QualityLabel.Text="正在读取原始分辨率…";
            foreach(var key in tiles.Keys.Where(k=>k.Item1<firstX || k.Item1>lastX || k.Item2<firstY || k.Item2>lastY).ToArray()){tiles[key].Dispose();tiles.Remove(key);}
            for(int y=firstY;y<=lastY;y++)for(int x=firstX;x<=lastX;x++)
            {
                token.ThrowIfCancellationRequested();if(tiles.ContainsKey((x,y)))continue;
                var reply=await previewWorker!.Request(SourcePath(selected),"fullTile",Context(selected,current),new(1024,1024,FrameIndex:animated?frameIndex:0,TileX:x,TileY:y,PageIndex:imagePage),token,Stamp(selected));
                var bitmap=await LoadRenderedBitmap(reply);if(current!=selection || token.IsCancellationRequested||tileReloadPending||animated&&(animationRunning||frameIndex!=animationFrameIndex)){bitmap.Dispose();return;}tiles[(x,y)]=bitmap;ImageCanvas.Invalidate();
            }
            QualityLabel.Text=$"原始分辨率 · {zoom*Shell.XamlRoot.RasterizationScale:P0} · {sourceWidth:N0} × {sourceHeight:N0}";
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(current==selection)ShowPreviewError(ex);}
        finally{tileGate.Release();if(tileReloadPending&&!closing){tileReloadPending=false;_=LoadVisibleTiles();}}
    }
    private async void Fit(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.Fit);
    private async void Actual(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.Actual);
    private async void Rotate(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.Rotate);
    private async void ContainerChanged(ListViewBase sender,ContainerContentChangingEventArgs e)
    {
        if(e.InRecycleQueue)
        {
            BindVisibleContainer(sender,e.ItemContainer,null);
            return;
        }
        if(closing||e.Item is not FileRow row||(!firstPageRows.Contains(row)&&(results is not {} source||source.IndexOf(row)<0)))return;
        BindVisibleContainer(sender,e.ItemContainer,row);await LoadThumbnail(sender,row);
    }
    private void BindVisibleContainer(ListViewBase view,DependencyObject container,FileRow? row)
    {
        var key=(view,container);
        if(visibleContainers.TryGetValue(key,out var old))
        {
            if(ReferenceEquals(old,row))return;
            visibleContainers.Remove(key);
            if(--visibleConsumerCounts[old]==0)
            {
                verifyRowRecycling?.Invoke(old,"last-consumer");
                visibleConsumerCounts.Remove(old);visible.Remove(old);
                // A group move temporarily recycles containers before the same
                // visible rows are realized at their restored scroll position.
                if(publicationRows?.Contains(old)!=true)
                {
                    if(thumbnailRequests.Remove(old,out var request)){request.Cancel();request.Dispose();}
                    if(propertyCancellations.TryGetValue(old,out var propertyCancellation))propertyCancellation.Cancel();
                    old.Thumbnail=null;
                }
            }
        }
        if(row is null)return;
        visibleContainers[key]=row;visibleConsumerCounts[row]=visibleConsumerCounts.GetValueOrDefault(row)+1;visible.Add(row);
    }
    private async Task LoadThumbnail(ListViewBase sender,FileRow row)
    {
        if(closing)return;
        if(sender==FilesList){await LoadRowProperties(row);return;}
        if(row.Thumbnail is not null||row.ThumbnailError.Length>0||thumbnailRequests.ContainsKey(row))return;
        thumbnailWorkCount++;var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);thumbnailRequests[row]=request;var token=request.Token;string activeRoot=root,activeId=rootId;long activeEpoch=epoch,activeGeneration=generation;bool slot=false;ThumbnailCacheLease? cacheLease=null;ImageReply? produced=null;WorkerClient? decoder=null;string? coverAsset=null;
        long stageStart=verifyThumbnailStage is null?0:Stopwatch.GetTimestamp();
        bool pipeline=false;ImageReply? metadataReply=null;
        SnapshotItem? requestedItem=null;
        bool OwnsRequest()=>!closing&&activeId==rootId&&activeEpoch==epoch&&!token.IsCancellationRequested
            &&visible.Contains(row)&&(firstPageRows.Contains(row)||results?.Contains(row)==true)
            &&thumbnailRequests.TryGetValue(row,out var owner)&&ReferenceEquals(owner,request);
        bool OwnsRow()=>OwnsRequest()&&requestedItem is {} expected&&row.Item is {} item&&item.EntryId==expected.EntryId&&item.Version==expected.Version
            &&item.RelativePath==expected.RelativePath&&item.Bytes==expected.Bytes&&item.Kind==expected.Kind;
        void ReturnDecoder(){if(decoder is not null){thumbnailPool.Enqueue(decoder);decoder=null;}if(slot){thumbnailSlots.Release();slot=false;}}
        void Mark(string stage){if(verifyThumbnailStage is {} record){record(stage,Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds);stageStart=Stopwatch.GetTimestamp();}}
        bool InViewport()=>IsThumbnailInViewport(sender,row);
        try
        {
            if(row.Item is null)await (results??throw new InvalidOperationException("当前结果已关闭。")).EnsureLoaded(row,token);requestedItem=row.Item;Mark("page");await thumbnailPipelines.WaitAsync(InViewport,token);pipeline=true;await thumbnailSlots.WaitAsync(token);slot=true;Mark("queue");if(!visible.Contains(row)||thumbnailWorker is null||!OwnsRow())return;
            // Each slot owns a separate decoder. Sharing one WorkerClient here
            // serializes both slots behind its IPC gate on every cold page.
            decoder=thumbnailPool.Dequeue();
            var properties=await ResolveRow(row,activeId,token);Mark("properties");
            string kind=row.Kind,source=SourcePath(row),asset;
            if(kind is not ("image" or "video" or "audio"))return;
            int edge=kind=="image"?ThumbnailCache.SelectEdge(Math.Max(256,ThumbnailSize.Value*Shell.XamlRoot.RasterizationScale)):ThumbnailSize.Value*Shell.XamlRoot.RasterizationScale>512?1024:512;
            var stat=(Length:properties.LogicalBytes,Modified:properties.ModifiedUtcTicks);
            if(stat.Length!=row.Item!.Bytes)throw new IOException("文件已变化，请刷新目录。");
            string representation=kind=="video"?"videoCover":kind=="audio"?"audioCover":FileKinds.Raw.Contains(Path.GetExtension(source))?"rawEmbedded":"thumbnail";
            var cacheKey=new ThumbnailCacheKey(row.Item.EntryId,row.Item.Version,stat.Modified,stat.Length,edge,kind is "video" or "audio"?providerIdentity+"/"+MediaTools.CoverStrategyVersion:providerIdentity,representation,SourceSignature:properties.SourceSignature);
            if(verifyThumbnailReadBarrier is not null)await verifyThumbnailReadBarrier(row,token);
            cacheLease=await thumbnailCache!.TryGet(cacheKey,token);Mark("cacheLookup");
            if(cacheLease is not null)
            {
                using var stream=cacheLease.OpenRead();var cached=new BitmapImage();await cached.SetSourceAsync(stream.AsRandomAccessStream());if(OwnsRow()){row.Thumbnail=cached;await ReadDemandedMetadata(row,token,decoder:decoder);if(OwnsRow())await ResolveRow(row,activeId,token);}return;
            }
            if(properties.HydrationState=="placeholder"&&!approvedCloud.Contains(CloudKey(row)))return;
            if(kind=="image")
            {
                var reply=await decoder.Request(source,representation,new(activeId,activeEpoch,activeGeneration,1,row.Item!.Version,1),new(edge,edge),token,Stamp(row));produced=reply;asset=reply.AssetPath!;Mark("decode");
                metadataReply=reply;if(!OwnsRow())return;var info=reply.Message.Metadata!.Value;row.DescribeImage(info.GetProperty("width").GetInt32(),info.GetProperty("height").GetInt32(),Path.GetExtension(source).TrimStart('.'));
            }
            else if(kind is "video" or "audio")
            {
                var info=await media!.Probe(source,token,WorkerPriority.Visible);await catalog!.ApplyMediaMetadata(row.Item.EntryId,row.Item.Version,SourceRootId(row),SourceRootEpoch(row),info,"ffprobe-v1",token);if(info.Details is {} mediaDetails)await catalog.ApplyFileDetails(row.Item.EntryId,row.Item.Version,SourceRootId(row),SourceRootEpoch(row),mediaDetails,"ffprobe-v1",token);await ResolveRow(row,activeId,token);if(kind=="audio"&&!info.HasCover)return;string directory=Path.Combine(RuntimeDataDirectory,"temp","covers");Directory.CreateDirectory(directory);asset=coverAsset=Path.Combine(directory,Guid.NewGuid().ToString("N")+".png");await media.Cover(source,asset,info,token,edge);
            }
            else return;
            var cacheWrite=await thumbnailCache.StoreOptional(cacheKey,asset,token);cacheLease=cacheWrite.Lease;Mark("cacheStore");
            if(!OwnsRow())return;
            if(cacheWrite.Warning is not null)RecordWebView("ThumbnailCache: "+cacheWrite.Warning);
            var bitmap=new BitmapImage();using(var imageStream=cacheLease?.OpenRead()??File.OpenRead(asset)){await bitmap.SetSourceAsync(imageStream.AsRandomAccessStream());}if(OwnsRow())row.Thumbnail=bitmap;
            Mark("bitmap");
            if(metadataReply is not null)
            {
                // The bitmap is loaded before releasing its worker-owned asset.
                // A later request may restart that worker and remove its old files.
                await decoder!.ReleaseAsset(produced!);produced=null;ReturnDecoder();
                if(verifyMetadataBarrier is not null)await verifyMetadataBarrier(token);
                await RecordMetadata(row,metadataReply,token);Mark("metadataWrite");
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(OwnsRow()||(requestedItem is null&&OwnsRequest()))row.FailThumbnail(ex);}
        finally{try{cacheLease?.Dispose();if(coverAsset is not null&&File.Exists(coverAsset))File.Delete(coverAsset);if(produced is not null)await decoder!.ReleaseAsset(produced);}catch(Exception ex){ShowError(ex);}finally{ReturnDecoder();if(pipeline)thumbnailPipelines.Release();if(thumbnailRequests.TryGetValue(row,out var value)&&ReferenceEquals(value,request)){thumbnailRequests.Remove(row);request.Dispose();}if(--thumbnailWorkCount==0&&closing)thumbnailsIdle.TrySetResult();}}
    }
    private async Task LoadRowProperties(FileRow row,bool refresh=false)
    {
        using var operation=browserWork.Enter();
        if(operation is null||closing||results is not {} source)return;
        string id=rootId;long requestedRoot=rootChangeVersion;
        do
        {
        if(refresh)propertyRefreshPending.Add(row);
        if(!propertyRequests.Add(row)){if(propertyCancellations.TryGetValue(row,out var pending)&&pending.IsCancellationRequested)propertyRetryPending.Add(row);return;}
        using var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        propertyCancellations[row]=request;
        var token=request.Token;bool slot=false;
        try
        {
            while(true)
            {
                try{await source.EnsureLoaded(row,token);break;}
                catch(OperationCanceledException) when(!token.IsCancellationRequested&&!closing&&results is {} current&&!ReferenceEquals(current,source)&&current.Contains(row))
                {
                    // Keep ownership of this row's single request while only its
                    // retired snapshot page read migrates to the current source.
                    source=results!;
                }
            }
            await thumbnailSlots.WaitAsync(token);slot=true;
            do
            {
                propertyRefreshPending.Remove(row);
                if(!visible.Contains(row)||id!=rootId||results?.Contains(row)!=true)break;
                token.ThrowIfCancellationRequested();
                var decoder=thumbnailPool.Dequeue();
                try{await ReadDemandedMetadata(row,token,decoder:decoder);}finally{thumbnailPool.Enqueue(decoder);}
                await ResolveRow(row,id,token);
                if(verifyVideoPropertiesRead is not null)await verifyVideoPropertiesRead(token);
            }while(propertyRefreshPending.Contains(row));
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!token.IsCancellationRequested)ShowError(ex);}
        finally
        {
            propertyCancellations.Remove(row);propertyRequests.Remove(row);propertyRefreshPending.Remove(row);if(slot)thumbnailSlots.Release();
            refresh=propertyRetryPending.Remove(row);
        }
        // The per-attempt CTS leaves scope before another attempt starts. One
        // owned operation covers the loop and shutdown waits for that owner.
        }
        while(refresh&&!closing&&requestedRoot==rootChangeVersion&&id==rootId&&visible.Contains(row)&&results is {} next&&next.Contains(row)&&(source=next) is not null);
    }
    private void CancelObsoleteThumbnails(VirtualResults next)
    {
        foreach(var (row,request) in thumbnailRequests.ToArray())
        {
            // A loaded, unchanged row owns its decode independently of the retired
            // snapshot. Only unfinished page reads need rebinding to the new page cache.
            if(row.Item is not null&&row.Ordinal>=0&&row.Ordinal<next.Count&&ReferenceEquals(next[(int)row.Ordinal],row))continue;
            thumbnailRequests.Remove(row);request.Cancel();request.Dispose();
        }
    }
    private void CancelThumbnails()
    {
        firstPageRows.Clear();
        firstPageSequence=[];firstPageFilter=null;
        foreach(var token in thumbnailRequests.Values){token.Cancel();token.Dispose();}
        thumbnailRequests.Clear();
        propertyRetryPending.Clear();foreach(var request in propertyCancellations.Values)request.Cancel();
        foreach(var row in visible)row.Thumbnail=null;
        visible.Clear();visibleContainers.Clear();visibleConsumerCounts.Clear();
    }
    private void ClearImage(){pendingAnimationBitmap?.Dispose();pendingAnimationBitmap=null;fitBitmap?.Dispose();fitBitmap=null;foreach(var tile in tiles.Values)tile.Dispose();tiles.Clear();sourceWidth=sourceHeight=0;zoom=0;pan=Vector2.Zero;ImageCanvas.Invalidate();}
    private async Task RecordMetadata(FileRow row,ImageReply reply,CancellationToken cancellation)
    {
        if(reply.Message.Quality=="rawEmbedded" || catalog is null || row.Item is null)return;
        var data=reply.Message.Metadata!.Value;var context=reply.Message.Context!;
        int pages=data.GetProperty("pages").GetInt32();
        bool applied=await catalog.ApplyImageMetadata(row.Item.EntryId,row.Item.Version,SourceRootId(row),SourceRootEpoch(row),data.GetProperty("width").GetInt32(),data.GetProperty("height").GetInt32(),data.GetProperty("format").GetString()!,data.GetProperty("isRaw").GetBoolean(),data.GetProperty("isAnimated").GetBoolean(),data.GetProperty("provider").GetString()!,cancellation,pages);
        if(applied&&pages>1&&!data.GetProperty("isRaw").GetBoolean()&&!data.GetProperty("isAnimated").GetBoolean()&&rootId==context.RootId&&epoch==context.RootEpoch)
        {
            var properties=await ResolveRow(row,context.RootId,cancellation);
            row.Thumbnail=null;
            if(ReferenceEquals(row,selected))selectedProperties=properties;
        }
    }
    private async Task LoadText(long offset,long current,CancellationToken cancellation)
    {
        if(selected is null)return;string path=SourcePath(selected),encoding=textEncoding!;long sessionVersion=textSessionGeneration,windowVersion=++textWindowGeneration;
        textWindowStop.Cancel();textWindowStop.Dispose();textWindowStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,textSessionStop.Token);var token=textWindowStop.Token;
        var page=await CurrentTextClient().ReadWindow(offset,32*1024,token);
        if(current!=selection||sessionVersion!=textSessionGeneration||windowVersion!=textWindowGeneration||token.IsCancellationRequested)return;previousSearch=null;displayedText=page;textStart=page.Start;textNext=page.Next;TextContent.Text=page.Text;MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;TextOffset.Value=page.Start;TextScroll.ChangeView(0,0,null,true);QualityLabel.Text=$"{page.Encoding} · 字节 {page.Start:N0}–{page.Next:N0} / {page.Length:N0}";UpdateReaderControls();
        previewFailure=null;if(!previewLoading&&loadingBadge is not null)loadingBadge.Visibility=Visibility.Collapsed;
        RecordPreviewStage(current,"textPresented");scanLog?.Write("text-window",new{request=current,encoding=page.Encoding,page.Start,page.Next,page.Length});
    }
    private async void TextNext(object sender,RoutedEventArgs e){try{await LoadText(textNext,selection,selectionStop.Token);}catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}}
    private async void TextPrevious(object sender,RoutedEventArgs e){try{await LoadText(Math.Max(0,textStart-32*1024),selection,selectionStop.Token);}catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}}
    private async void TextJump(object sender,RoutedEventArgs e){try{await LoadText(checked((long)TextOffset.Value),selection,selectionStop.Token);}catch(Exception ex){ShowPreviewError(ex);}}
    private async void TextSearch(object sender,RoutedEventArgs e)
    {
        try{await FindText();}
        catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}
    }
    private async void ChangeTextEncoding(object sender,SelectionChangedEventArgs e)
    {
        if(TextEncoding.SelectedItem is not ComboBoxItem item||restoringTextEncoding)return;textEncoding=item.Tag.ToString()=="auto"?null:item.Tag.ToString();
        if(selected?.Item is null||selected.Kind is not ("text" or "markdown"))return;long current=selection,offset=textStart;bool rendered=MarkdownHost.Visibility==Visibility.Visible;
        try{await ResetTextSession();await LoadText(offset,current,selectionStop.Token);if(current!=selection)return;await StartTextIndex(current,selectionStop.Token);if(rendered)await LoadMarkdown(current,selectionStop.Token);}
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection)ShowPreviewError(ex);}
    }
    private async void ToggleMarkdown(object sender,RoutedEventArgs e){if(MarkdownHost.Visibility==Visibility.Visible){MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;}else if(selected is not null&&selected.Kind=="markdown")await LoadMarkdown(selection,selectionStop.Token);UpdateReaderControls();}
    private async Task LoadMarkdown(long current,CancellationToken token)
    {
        if(selected?.Item is null||contentWorker is null)return;
        var documentRow=selected;string document=SourcePath(documentRow),documentRoot=SourceRootPath(documentRow),documentRootId=SourceRootId(documentRow);long documentEpoch=SourceRootEpoch(documentRow);
        ImageReply? reply=null;bool entered=false,initializing=false;string markdownStage="render";
        try
        {
            await markdownLoadGate.WaitAsync(token);entered=true;if(current!=selection||closing)return;
            markdownLoading=true;ScheduleMarkdownRelease();
            reply=await contentWorker.RenderMarkdown(document,Context(selected,current),token,textEncoding,Stamp(selected));if(current!=selection)return;
            markdownImages.Clear();int resourceIndex=0;long resourceBytes=0,resourcePixels=0;
            foreach(var resource in reply.Message.Metadata!.Value.GetProperty("resources").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();try
                {
                    string path=await prefetchSourceProbe.ResolveImage(documentRoot,document,resource.GetProperty("relativeUrl").GetString()!,token);
                    var imageStamp=await prefetchSourceProbe.Read(path,token);
                    await thumbnailSlots.WaitAsync(token);
                    var imageWorker=thumbnailPool.Dequeue();ImageReply? image=null;
                    try
                    {
                        image=await imageWorker.Request(path,"thumbnail",new(documentRootId,documentEpoch,generation,current,1,1),new(1024,1024),token,imageStamp);
                        if(current!=selection)return;
                        if(await prefetchSourceProbe.Read(path,token)!=imageStamp)throw new IOException("Markdown 图片已发生变化。");
                        long bytes=new FileInfo(image.AssetPath!).Length,pixels=1024L*1024;
                        if(resourceBytes+bytes>32L*1024*1024||resourcePixels+pixels>32L*1024*1024){Status.Text="此文档的图片较多，部分图片未加载。";break;}
                        markdownImages[resource.GetProperty("token").GetString()!]=await File.ReadAllBytesAsync(image.AssetPath!,token);resourceBytes+=bytes;resourcePixels+=pixels;if(++resourceIndex>=200)break;
                    }
                    finally{try{if(image is not null)await imageWorker.ReleaseAsset(image);}finally{thumbnailPool.Enqueue(imageWorker);thumbnailSlots.Release();}}
                }
                catch(Exception ex) when(ex is UnauthorizedAccessException or IOException or NotSupportedException or TimeoutException){if(current==selection&&!closing)Status.Text="部分 Markdown 图片无法显示；已保留文档内容。";}
            }
            if(markdown is null)
            {
                initializing=true;markdown=new WebView2();var view=markdown;MarkdownHost.Content=markdown;MarkdownHost.Visibility=Visibility.Visible;
                markdownStage="environment";string fixedRuntime=Path.Combine(AppContext.BaseDirectory,"runtime","webview2");var environment=await CoreWebView2Environment.CreateWithOptionsAsync(Directory.Exists(fixedRuntime)?fixedRuntime:null,Path.Combine(RuntimeDataDirectory,"webview"),null).AsTask().WaitAsync(TimeSpan.FromSeconds(5),token);markdownStage="controller";await markdown.EnsureCoreWebView2Async(environment).AsTask().WaitAsync(TimeSpan.FromSeconds(5),token);
                token.ThrowIfCancellationRequested();var core=markdown.CoreWebView2;core.Settings.IsScriptEnabled=false;core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;core.Settings.AreDevToolsEnabled=false;
                core.NavigationCompleted+=(_,e)=>{if(!ReferenceEquals(markdown,view))return;RecordWebView($"NavigationCompleted success={e.IsSuccess} error={e.WebErrorStatus}");if(e.NavigationId==markdownNavigationId)navigationComplete?.TrySetResult(e.IsSuccess);};
                core.ProcessFailed+=(_,e)=>
                {
                    if(!ReferenceEquals(markdown,view)||closing)return;
                    RecordWebView($"ProcessFailed {e.ProcessFailedKind}");navigationComplete?.TrySetException(new IOException("Markdown 显示进程失败。"));
                    if(!markdownLoading)DispatcherQueue.TryEnqueue(()=>{if(ReferenceEquals(markdown,view)&&!closing){bool visible=MarkdownHost.Visibility==Visibility.Visible;ReleaseMarkdownView();if(visible){MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;QualityLabel.Text="Markdown 显示失败，已保留原文。可再次切换排版重试。";}}});
                };
                core.NewWindowRequested+=(_,e)=>e.Handled=true;core.DownloadStarting+=(_,e)=>e.Cancel=true;core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
                core.NavigationStarting+=(_,e)=>{if(!ReferenceEquals(markdown,view)||closing){e.Cancel=true;return;}RecordWebView($"NavigationStarting user={e.IsUserInitiated}");if(markdownDocumentUrl.Length>0&&e.Uri.Split('#')[0]==markdownDocumentUrl){markdownNavigationId=e.NavigationId;return;}e.Cancel=true;if(e.IsUserInitiated&&Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http" && uri.Host!="folderlens.local")Process.Start(new ProcessStartInfo(e.Uri){UseShellExecute=true});};
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>
                {
                    if(!ReferenceEquals(markdown,view)||closing){e.Response=environment.CreateWebResourceResponse(null,403,"Forbidden","");return;}
                    using var deferral=e.GetDeferral();var uri=new Uri(e.Request.Uri);string key=uri.Segments.LastOrDefault()??"";
                    RecordWebView($"Resource context={e.ResourceContext} local={uri.Host=="folderlens.local"}");
                    if(e.ResourceContext==CoreWebView2WebResourceContext.Document&&e.Request.Uri==markdownDocumentUrl)e.Response=environment.CreateWebResourceResponse(new MemoryStream(markdownDocument,false).AsRandomAccessStream(),200,"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
                    else if(uri.Scheme=="https"&&uri.Host=="folderlens.local"&&markdownImages.TryGetValue(key,out var bytes)){e.Response=environment.CreateWebResourceResponse(new MemoryStream(bytes,false).AsRandomAccessStream(),200,"OK","Content-Type: image/png\r\nCache-Control: no-store");}
                    else e.Response=environment.CreateWebResourceResponse(new MemoryStream().AsRandomAccessStream(),403,"Forbidden","Content-Type: text/plain");
                };
                initializing=false;
            }
            if(current!=selection)return;markdownStage="navigation";markdownDocument=await File.ReadAllBytesAsync(reply.AssetPath!,token);markdownDocumentUrl="https://folderlens.local/document/"+Guid.NewGuid().ToString("N");navigationComplete=new(TaskCreationOptions.RunContinuationsAsynchronously);markdown.CoreWebView2.Navigate(markdownDocumentUrl);MarkdownHost.Visibility=Visibility.Visible;TextScroll.Visibility=Visibility.Collapsed;QualityLabel.Text="正在显示 Markdown…";if(!await navigationComplete.Task.WaitAsync(TimeSpan.FromSeconds(5),token))throw new IOException("Markdown 导航失败。");if(current==selection){await ApplyMarkdownTextSize();QualityLabel.Text="Markdown 排版 · 未加载网络资源";}
        }
        catch(OperationCanceledException){if(entered&&initializing)ReleaseMarkdownView();}
        catch(Exception ex){RecordWebView($"MarkdownFailure stage={markdownStage} type={ex.GetType().Name}");if(entered)ReleaseMarkdownView();if(current==selection&&!closing){MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;QualityLabel.Text=$"无法按排版显示，已改为纯文本。{UserMessages.Error(ex)}";}}
        finally{try{if(reply is not null)await contentWorker.ReleaseAsset(reply);}finally{if(entered){markdownLoading=false;UpdateReaderControls();ScheduleMarkdownRelease();markdownLoadGate.Release();}}}
    }
    private void RecordWebView(string message){if(webviewEvents.Count>=128)webviewEvents.RemoveAt(0);webviewEvents.Add(message);}
    private void Navigate(int delta){int count=results?.Count??firstPageSequence.Length;if(count==0)return;int index=selected is null?(delta<0?count-1:0):Math.Clamp((int)selected.Ordinal+delta,0,count-1);var row=results is not null?(FileRow)results[index]!:firstPageSequence[index];RevealBrowserRow(row);ActiveBrowser.SelectedItem=row;if(!immersive)ActiveBrowser.ScrollIntoView(row);if(viewerTop?.Visibility==Visibility.Visible&&viewerStrip is not null){viewerStrip.SelectedIndex=index;viewerStrip.ScrollIntoView(row);}}
    private void Previous(object sender,RoutedEventArgs e)=>Navigate(-1);private void Next(object sender,RoutedEventArgs e)=>Navigate(1);
    private void CopyPath(object sender,RoutedEventArgs e){if(selected is null)return;try{writePreviewClipboard(SourcePath(selected));}catch(Exception error){ShowError(error);}}
    private void Reveal(object sender,RoutedEventArgs e){if(selected is null)return;var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=false};start.ArgumentList.Add("/select,"+SourcePath(selected));try{Process.Start(start);}catch(Exception ex){ShowError(ex);}}
    private async void ExternalOpen(object sender,RoutedEventArgs e)
    {
        if(selected is not {Item:not null} row||catalog is not {} store)return;
        long current=selection,sourceEpoch=epoch;string sourceRoot=root,sourceId=rootId;var token=selectionStop.Token;
        try
        {
            await ResolveRow(row,sourceId,token);
            if(current!=selection||sourceEpoch!=epoch||sourceId!=rootId||closing||token.IsCancellationRequested)return;
            if(!await EnsureCloudRead(row,current)||closing)return;
            var target=await ExternalFileLaunch.Resolve(store,prefetchSourceProbe,SourceRootPath(row),SourceRootId(row),row.Item.EntryId,row.Item.Version,approvedCloud.Contains(CloudKey(row)),token);
            if(current!=selection||sourceEpoch!=epoch||sourceId!=rootId||sourceRoot!=root||closing||token.IsCancellationRequested)return;
            if(target.Kind is "video" or "audio")StartPlayer(target.Path);
            else Process.Start(ExternalFileLaunch.CreateStartInfo(target.Path,null));
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(current==selection&&!closing)ShowError(ex);}
    }
    private async void OpenSelected(object sender,DoubleTappedRoutedEventArgs e){if(selected is null)return;if(selected.Kind=="image")await EnterFullScreen();else if(selected.Kind is "text" or "markdown")await SetImmersive(true);else if(selected.Kind=="audio")PlayAudio(sender,new());else if(selected.Kind=="video"&&internalVideoByDefault){await SetImmersive(true);await PlayVideoCore();}else ExternalOpen(sender,new RoutedEventArgs());}
    private async void SaveView(object sender,RoutedEventArgs e)
    {
        try
        {
            var filter=CurrentFilter();var views=await settings!.Load<Dictionary<string,SavedView>>("views.json")??[];var name=new TextBox{Header="浏览设置名称",Text="我的浏览设置",MaxLength=100};var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="保存浏览设置",Content=name,PrimaryButtonText="保存",CloseButtonText="取消"};if(await dialog.ShowAsync()!=ContentDialogResult.Primary||string.IsNullOrWhiteSpace(name.Text))return;
            if(views.ContainsKey(name.Text)){var confirm=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="替换同名浏览设置？",Content=name.Text,PrimaryButtonText="替换",CloseButtonText="取消"};if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;}
            views[name.Text]=CaptureView();await settings.Save("views.json",views);Status.Text="浏览设置已保存。";
        }catch(Exception ex){ShowError(ex);}
    }
    private async void RestoreView(object sender,RoutedEventArgs e)
    {
        try{await ManageViews();}catch(Exception ex){ShowError(ex);}
    }
    private static void SelectTag(ComboBox box,string tag){foreach(ComboBoxItem item in box.Items)if(item.Tag.ToString()==tag){box.SelectedItem=item;break;}}
    public sealed record SavedView(string Root,FilterSpec Filter,string? SelectedPath=null,double ScrollOffset=0,bool Details=false,string? ScrollAnchorPath=null,PreviewBookmark? Preview=null);
    private void ShowError(Exception ex){if(!closing)Status.Text=$"操作未完成：{UserMessages.Error(ex)}";}
    private void ShowPreviewError(Exception ex)
    {
        RecordPreviewFailure(ex);
        if(!closing&&ReportDeviceLoss(ex))return;
        if(closing)return;
        previewFailure=$"无法预览：{UserMessages.Error(ex)}";QualityLabel.Text=previewFailure;
        if(loadingBadge is not null){loadingText!.Text=previewFailure;loadingBadge.Visibility=Visibility.Visible;}
    }
    private bool finalWindowClose;
    private Task? shutdownTask;
    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if(finalWindowClose)return;args.Cancel=true;_=RequestShutdown();
    }
    private Task RequestShutdown()=>shutdownTask??=Shutdown();
    private async Task Shutdown()
    {
        closing=true;controlsReady=false;scanLogTimer?.Stop();DatabaseExecutor.OperationMeasured=null;WorkerResources.Shared.MemoryPressure-=OnPrefetchMemoryPressure;var retired=browserWork.Stop();
        async Task Cleanup(Func<Task> action){try{await action();}catch(OperationCanceledException){}catch(Exception ex){RecordWebView("ShutdownError "+ex.GetType().Name+" "+ex.HResult);}}
        try
        {
            SavedView? lastSession=null;await Cleanup(()=>{lastSession=CaptureClosingView();return Task.CompletedTask;});
            foreach(Action action in new Action[]{()=>lifetime.Cancel(),()=>scanStop.Cancel(),()=>queryStop.Cancel(),()=>selectionStop.Cancel(),()=>prefetchStop.Cancel(),()=>physicalTreeStop.Cancel(),()=>ResetViewerGesture(),()=>fitResizeTimer?.Stop(),()=>viewerIdleTimer?.Stop(),()=>viewerGroupTimer?.Stop(),CancelThumbnails,()=>searchTimer?.Stop(),()=>slideTimer?.Stop(),()=>monitor?.Dispose(),()=>animationTimer?.Stop(),StopVideo,StopAudio})
                await Cleanup(()=>{action();return Task.CompletedTask;});
            await Cleanup(CloseCapacityWindow);
            if(lastSession is not null)await Cleanup(()=>SaveLastSession(lastSession));if(settings is not null)await Cleanup(()=>settings.Save("desktop.json",new DesktopState(ThumbnailSize.Value,gridShowPaths,PreviewColumn.Width.Value,DetailsMode.IsChecked==true,treeFraction)));
            await Cleanup(()=>retired);
            await Cleanup(RetireBackgroundScans);if(scanLog is not null)await Cleanup(()=>scanLog.DisposeAsync().AsTask());
            await Cleanup(DisposeMarkdownView);await Cleanup(DisposeTextSession);
            foreach(var task in new[]{scanTask,metadataTask,prefetchTask,capabilityTask,treeRefreshTask,physicalTreeTask})if(task is not null)await Cleanup(()=>task);
            if(thumbnailWorkCount>0)await Cleanup(()=>thumbnailsIdle.Task);
            await Cleanup(()=>PruneTreeListings(all:true));
            await Cleanup(()=>{results?.Dispose();results=null;ClearPrefetchedImages();ClearImage();ImageCanvas.RemoveFromVisualTree();return Task.CompletedTask;});
            if(catalog is not null&&resultHandle is {} displayed){resultHandle=null;await Cleanup(()=>catalog.ReleaseSnapshot(displayed.Id));}
            if(verifyClosingState is not null)await Cleanup(verifyClosingState);
            await Cleanup(()=>prefetchSourceProbe.DisposeAsync().AsTask());
            foreach(var worker in new[]{prefetchWorker,previewWorker,thumbnailWorker,secondThumbnailWorker,metadataWorker,contentWorker}.Concat(extraThumbnailWorkers))if(worker is not null)await Cleanup(()=>worker.DisposeAsync().AsTask());
            if(thumbnailCache is not null)await Cleanup(()=>thumbnailCache.DisposeAsync().AsTask());
            if(browsingStorage is not null)await Cleanup(()=>browsingStorage.DisposeAsync().AsTask());else if(catalog is not null)await Cleanup(()=>catalog.DisposeAsync().AsTask());
        }
        finally{try{if(Environment.GetCommandLineArgs().Contains("--diagnostic-ui"))await File.WriteAllLinesAsync(Path.Combine(dataDirectory,"webview-events.log"),webviewEvents);}finally{if(InstanceBroker is not null)await InstanceBroker.DisposeAsync();finalWindowClose=true;Close();Application.Current.Exit();}}
    }
}
