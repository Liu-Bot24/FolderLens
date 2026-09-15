using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string,string> verificationComponents=[];
    private async Task FailVerificationInitialization(Exception error)
    {
        Environment.ExitCode=1;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-refresh.json"),JsonSerializer.Serialize(new {status="FAIL",phase="initialize",error=error.ToString(),components=verificationComponents}));
        }
        catch(Exception reportError){Console.Error.WriteLine(error);Console.Error.WriteLine(reportError);}
        finally{PostVerificationClose(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0010,0,0);}
    }
    private Action? verifyPublishFault;
    private Func<CancellationToken,Task>? verifyCandidateBarrier,verifyScanBarrier,verifyPageBarrier,verifyPreviewBarrier,verifyFirstPageBarrier;
    private Func<Task>? verifyClosingState;
    private Func<long,Task>? verifyTreePageReadBarrier;
    private Action<FileRow,string>? verifyRowRecycling;
    private Action<Exception>? verifyPublicationFailure;
    private Action<double>? verifyPublicationDuration;
    private Action<IReadOnlyDictionary<string,double>>? verifyPublicationStages;
    private Func<Task>? verifyRetirementBarrier;
    private Action? verifySlideEventCompleted;
    private Action? verifySlideTickCompleted;
    private Func<FileRow,CancellationToken,Task>? verifyThumbnailReadBarrier;
    private Func<Task>? verifySelectionRestoreBarrier;
    private Func<CancellationToken,Task>? verifySlideAdvanceBarrier;
    private Func<CancellationToken,Task>? verifyMetadataBarrier;
    // Explicit, isolated native regression run. No user catalog or source folder is used.
    private async Task VerifyRefresh()
    {
        var report=new Dictionary<string,object>{{"components",verificationComponents},{"deployedVerification",Environment.GetCommandLineArgs().Contains("--verify-deployed")}};var timer=Stopwatch.StartNew();
        try
        {
            var arguments=Environment.GetCommandLineArgs();int gallery=Array.IndexOf(arguments,"--verify-gallery");
            int bitmapAssets=Array.IndexOf(arguments,"--verify-bitmap-assets");
            if(bitmapAssets>=0){await VerifyBitmapAssets(arguments[bitmapAssets+1],report);return;}
            int switching=Array.IndexOf(arguments,"--verify-image-switch");
            if(switching>=0){await VerifyImageSwitch(arguments[switching+1],report);return;}
            int categorySwitch=Array.IndexOf(arguments,"--verify-category-switch");
            if(categorySwitch>=0){await VerifyCategorySwitch(arguments[categorySwitch+1],report);return;}
            if(gallery>=0){if(gallery+1>=arguments.Length)throw new ArgumentException("缺少只读源目录。");await VerifyGallery(arguments[gallery+1],report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-tree-scale")){VerifyTreeScale(report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-group-order")){VerifyGroupOrder(report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-native-ranges")){await VerifyNativeRanges(report);return;}
            if(arguments.Contains("--verify-toolbar-widths")){await VerifyToolbarWidths(report);return;}
            if(arguments.Contains("--verify-preview-layout")){await VerifyPreviewLayout(report);return;}
            if(arguments.Contains("--verify-filter-editor")){await VerifyFilterEditor(report);return;}
            if(arguments.Contains("--verify-group-lazy")){await VerifyGroupLaziness(report);return;}
            if(arguments.Contains("--verify-range-publication-cost")){await VerifyRangePublicationCost(report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-r2-identity")){VerifyResultIdentity(report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-identity-scale")){VerifyIdentityScale(report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-r2-tree-limit")){await VerifyTreeLimit(report);return;}
            string source=Path.Combine(dataDirectory,"fixture"),first=Path.Combine(source,"A"),second=Path.Combine(source,"B");Directory.CreateDirectory(first);Directory.CreateDirectory(second);
            using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            byte[] pixels=new byte[64*48*4];for(int index=0;index<pixels.Length;index+=4){pixels[index]=40;pixels[index+1]=100;pixels[index+2]=200;pixels[index+3]=255;}
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,48,96,96,pixels);await encoder.FlushAsync();stream.Seek(0);byte[] png=new byte[checked((int)stream.Size)];await stream.ReadAsync(png.AsBuffer(),(uint)png.Length,InputStreamOptions.None);
            for(int index=0;index<12;index++)await File.WriteAllBytesAsync(Path.Combine(first,$"image-{index:D2}.png"),png);
            if(arguments.Contains("--verify-startup-profile")){await VerifyStartupProfile(source,report);return;}
            if(arguments.Contains("--verify-preview-completion")){await VerifyPreviewCompletion(source,report);return;}
            if(arguments.Contains("--verify-scan-pipeline")){await VerifyScanPipeline(source,report);return;}
            if(arguments.Contains("--verify-demand-audit")||arguments.Contains("--verify-file-operation-close")){await VerifyDemandAudit(source,report);return;}
            if(arguments.Contains("--verify-demand-layout")){await VerifyDemandLayout(source,report);return;}
            if(arguments.Contains("--verify-pro-scan-closeout")){await VerifyProScanCloseout(source,report);return;}
            if(arguments.Contains("--verify-format-choices")){await VerifyFormatChoices(source,report);return;}
            if(arguments.Contains("--verify-navigation-roots")){await VerifyNavigationRoots(source,report);return;}
            if(arguments.Contains("--verify-filter-panel")){await VerifyFilterPanelLayout(report);return;}
            if(arguments.Contains("--verify-thumbnail-priority")){await VerifyThumbnailPriority(source,report);return;}
            if(arguments.Contains("--verify-menu-availability")){await VerifyMenuAvailability(source,report);return;}
            if(arguments.Contains("--verify-prefetch-adoption")){await VerifyPrefetchAdoption(source,report);return;}
            if(arguments.Contains("--verify-wheel-distance")){await VerifyWheelDistance(source,report);return;}
            if(arguments.Contains("--verify-prepared-cache")){await VerifyPreparedCache(source,report);return;}
            if(arguments.Contains("--verify-prefetch-turnaround")){await VerifyPrefetchTurnaround(source,report);return;}
            if(arguments.Any(arg=>arg.StartsWith("--verify-scan-audit-"))){await VerifyScanAudit(source,report);return;}
            if(arguments.Contains("--verify-promotion-viewport")){await VerifyPromotionViewport(source,report);return;}
            if(arguments.Contains("--verify-player-settings")){await VerifyPlayerSettings(report);return;}
            if(arguments.Contains("--verify-audio-recovery")){await VerifyAudioRecovery(source,report);return;}
            if(arguments.Contains("--verify-collection-contract")){VerifyCollectionContract(report);return;}
            if(arguments.Contains("--verify-display-compatibility")){await VerifyDisplayCompatibility(source,report);return;}
            if(arguments.Any(arg=>arg.StartsWith("--verify-first-audit-"))){await VerifyFirstPageAudit(source,report);return;}
            if(arguments.Contains("--verify-capacity-ui")){await VerifyCapacityUi(source,report);return;}
            if(arguments.Contains("--verify-empty-audit")){await VerifyEmptyStateAudit(source,report);return;}
            if(arguments.Contains("--verify-empty-state")){await VerifyBrowserEmptyState(source,report);return;}
            if(arguments.Contains("--verify-video-card")){await VerifyVideoCard(source,report);return;}
            if(arguments.Contains("--verify-theme")){await VerifyTheme(source,report);return;}
            if(arguments.Contains("--verify-keyboard-completion")){await VerifyKeyboardCompletion(source,report);return;}
            if(arguments.Contains("--verify-tree-current-folder")){await VerifyTreeCurrentFolder(source,png,report);return;}
            if(arguments.Contains("--verify-press-gesture")){await VerifyPressGesture(source,report);return;}
            if(arguments.Contains("--verify-fit-lock")){await VerifyFitLock(source,report);return;}
            if(arguments.Contains("--verify-first-page")){await VerifyFirstPage(source,report);return;}
            if(arguments.Contains("--verify-text-reader")){await VerifyTextReader(source,report);return;}
            if(arguments.Contains("--verify-selection-appearance")){await VerifySelectionAppearance(source,report);return;}
            if(arguments.Contains("--verify-directory-filter")){await VerifyDirectoryFilter(source,report);return;}
            if(arguments.Contains("--verify-filmstrip")){await VerifyFilmstrip(report);return;}
            if(arguments.Contains("--verify-group-collapse")){await VerifyGroupCollapse(source,png,report);return;}
            if(arguments.Contains("--verify-viewer-information")){await VerifyViewerInformation(source,report);return;}
            if(arguments.Contains("--verify-browser-status")){await VerifyBrowserStatusBar(source,report);return;}
            if(arguments.Contains("--verify-collections")){await VerifyCollections(source,report);return;}
            if(arguments.Contains("--verify-quick-collections")){await VerifyQuickCollections(source,report);return;}
            if(arguments.Contains("--verify-collection-observation")){await VerifyCollectionObservationRefresh(source,report);return;}
            if(arguments.Contains("--verify-pro-feedback")){await VerifyProFeedback(source,report);return;}
            if(arguments.Contains("--verify-collection-markdown")){await VerifyCollectionMarkdown(source,report);return;}
            if(arguments.Contains("--verify-collection-pending-close")){await VerifyCollectionPendingClose(source,report);return;}
            if(arguments.Contains("--verify-tree-selection-visible")){await VerifyTreeSelectionVisible(source,png,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-container-recycle")){await VerifyContainerRecycle(source,first,png,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-preview-close")){await VerifyPreviewClose(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-preview-pending-close")){await VerifyPendingPreviewClose(source,false,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-refresh-metadata")){await VerifyRefreshMetadata(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-retired-row")){await VerifyRetiredRow(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-thumbnail-upgrade")){await VerifyThumbnailUpgrade(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-page-mode")||Environment.GetCommandLineArgs().Contains("--verify-page-pending-close")){await VerifyPageMode(pixels,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-tree-page-order")){await VerifyTreePageOrder(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-tree-abrupt-exit")||Environment.GetCommandLineArgs().Contains("--verify-tree-recovery")){await VerifyTreeRecovery(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-r2-close-query")||Environment.GetCommandLineArgs().Contains("--verify-r2-close-root")){await VerifyClosingWork(source,report);return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-r2-flow")){await VerifyAuditFlow(source,png,report);return;}
            if(arguments.Contains("--verify-group-publication")){await VerifyGroupPublication(png,report);return;}
            if(arguments.Contains("--verify-visible-group-move")){await VerifyVisibleGroupMove(source,png,report);return;}
            if(arguments.Contains("--verify-audit-three")){await VerifyAuditThree(source,report);return;}
            if(arguments.Contains("--verify-active-view-entry")){await VerifyActiveViewEntry(source,report);return;}
            if(arguments.Contains("--verify-selection-race")){await VerifySelectionRace(source,report);return;}
            if(arguments.Contains("--verify-slide-error")){await VerifySlideError(source,report);return;}
            if(arguments.Contains("--verify-slide-tick-race")){await VerifySlideTickRace(source,report);return;}
            if(arguments.Contains("--verify-root-from-viewer")){await VerifyRootFromViewer(source,first,report);return;}
            if(arguments.Contains("--verify-cache-fallback")){await VerifyCacheFallback(source,report);return;}
            if(arguments.Contains("--verify-thumbnail-bounds")){await VerifyThumbnailBounds(source,report);return;}
            folderGrouping=new(true);UpdateGroupingButton();await OpenRoot(source);
            // Automatic initialization and the scan must be done before observing steady state.
            if(metadataTask is not null)await metadataTask;await RefreshQuery();
            await WaitUntil(()=>visible.Count>0&&visible.All(row=>row.Thumbnail is not null),TimeSpan.FromSeconds(20));
            var row=visible.Where(row=>row.Thumbnail is not null).OrderBy(row=>row.Ordinal).First();
            var thumbnail=row.Thumbnail;var container=FilesGrid.ContainerFromItem(row);var sourceObject=FilesGrid.ItemsSource;
            if(container is null)throw new InvalidOperationException("真实 GridView 容器尚未创建。");
            int frames=0,missingFrames=0,containerChanges=0;
            void Observe(object? sender,object args)
            {
                frames++;if(!ReferenceEquals(row.Thumbnail,thumbnail))missingFrames++;
                if(!ReferenceEquals(FilesGrid.ContainerFromItem(row),container))containerChanges++;
            }
            CompositionTarget.Rendering+=Observe;
            try
            {
                bool resetBaseline=Environment.GetCommandLineArgs().Contains("--verify-refresh-reset");
                for(int iteration=0;iteration<4;iteration++){await RefreshQuery(scanPreview:!resetBaseline);await Task.Delay(100);}
                await FillCurrentMetadata();await Task.Delay(100);
                if(!ReferenceEquals(sourceObject,FilesGrid.ItemsSource))throw new InvalidOperationException("元数据完成时替换了列表 ItemsSource。");
                report["metadataCompletionRetained"]=true;
                await File.WriteAllBytesAsync(Path.Combine(second,"new.png"),png);
                await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);
                await RefreshQuery(scanPreview:true);await Task.Delay(200);
                if(resultHandle?.Count!=13||browserGroups?.Count!=2)throw new InvalidOperationException($"新增分组未进入真实列表。items={resultHandle?.Count}; groups={browserGroups?.Count}; busy={queryBusy}; selected={selected is not null}; status={Status.Text}; summary={ResultSummary.Text}");
                await File.WriteAllBytesAsync(Path.Combine(first,"image-99.png"),png);
                await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);
                await RefreshQuery(scanPreview:true);await Task.Delay(200);
                if(resultHandle?.Count!=14)throw new InvalidOperationException("现有分组新增图片未进入真实列表。");
                if(!ReferenceEquals(sourceObject,FilesGrid.ItemsSource))throw new InvalidOperationException("扫描刷新替换了 ItemsSource。");
                if(!ReferenceEquals(row.Thumbnail,thumbnail))throw new InvalidOperationException("未变化图片的缩略图被清除。");
                if(!ReferenceEquals(FilesGrid.ContainerFromItem(row),container))throw new InvalidOperationException("未变化图片的真实容器被替换。");
                if(frames==0||missingFrames>0||containerChanges>0)throw new InvalidOperationException($"渲染帧不稳定：{frames}/{missingFrames}/{containerChanges}");
                report["frames"]=frames;report["missingThumbnailFrames"]=missingFrames;report["changedContainerFrames"]=containerChanges;
                report["itemsSourceRetained"]=true;report["groups"]=browserGroups.Count;report["items"]=resultHandle.Count;report["status"]="PASS";
            }
            finally{CompositionTarget.Rendering-=Observe;}
            folderGrouping=new();UpdateGroupingButton();await RefreshQuery();
            report["flatInitialStaleVisibleRows"]=visible.Count(item=>flatBrowserItems!.IndexOf(item)<0||FilesGrid.ContainerFromItem(item) is null);
            await WaitUntil(()=>visible.Count>0&&visible.All(item=>item.Thumbnail is not null&&flatBrowserItems!.IndexOf(item)>=0&&FilesGrid.ContainerFromItem(item) is not null),TimeSpan.FromSeconds(10));
            var flatRow=visible.OrderBy(item=>item.Ordinal).First();var flatContainer=FilesGrid.ContainerFromItem(flatRow);var flatThumbnail=flatRow.Thumbnail;var flatSource=FilesGrid.ItemsSource;
            await RefreshQuery(scanPreview:true);await Task.Delay(100);
            await File.WriteAllBytesAsync(Path.Combine(second,"z-last.png"),png);
            await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);await RefreshQuery(scanPreview:true);await Task.Delay(100);
            if(resultHandle?.Count!=15||!ReferenceEquals(FilesGrid.ItemsSource,flatSource)||!ReferenceEquals(FilesGrid.ContainerFromItem(flatRow),flatContainer)||!ReferenceEquals(flatRow.Thumbnail,flatThumbnail))throw new InvalidOperationException("未分组列表刷新破坏了已有容器或缩略图。");
            report["flatListRetained"]=true;report["flatItems"]=resultHandle.Count;
            // Hold all real decoder slots to reproduce a cold/queued visible request.
            for(int slot=0;slot<WorkerResources.Shared.ThumbnailConcurrency;slot++)await thumbnailSlots.WaitAsync();
            Task pendingThumbnail;
            try
            {
                flatRow.Thumbnail=null;pendingThumbnail=LoadThumbnail(FilesGrid,flatRow);
                var pendingRequest=thumbnailRequests[flatRow];
                for(int iteration=0;iteration<4;iteration++)await RefreshQuery(scanPreview:true);
                if(!thumbnailRequests.TryGetValue(flatRow,out var surviving)||!ReferenceEquals(surviving,pendingRequest))throw new InvalidOperationException("增量刷新重启了未完成的可见缩略图任务。");
            }
            finally{thumbnailSlots.Release(WorkerResources.Shared.ThumbnailConcurrency);}
            await pendingThumbnail;
            if(flatRow.Thumbnail is null)throw new InvalidOperationException("保留的缩略图请求没有完成。");
            report["pendingThumbnailRetained"]=true;
            await OpenRoot(first);if(physicalTreeTask is not null)await physicalTreeTask;
            if(resultHandle?.Count!=13||RootPath.Text!=first)throw new InvalidOperationException("进入明确含图的子目录后结果未联动。");
            var siblings=activeTreeRoot?.Parent?.Children.Select(node=>(node.Content as FolderNode)?.Path).ToArray()??[];
            if(!siblings.Contains(first)||!siblings.Contains(second))throw new InvalidOperationException("祖先目录未显示完整兄弟文件夹。");
            await OpenRoot(second);if(resultHandle?.Count!=2)throw new InvalidOperationException("切换另一目录仍显示旧结果。");
            await NavigateHistory(false);if(root!=first||resultHandle?.Count!=13)throw new InvalidOperationException("返回未恢复对应目录结果。");
            await NavigateHistory(true);if(root!=second||resultHandle?.Count!=2)throw new InvalidOperationException("前进未恢复对应目录结果。");
            report["directoryTreeAndHistory"]=true;
            string third=Path.Combine(source,"C");Directory.CreateDirectory(third);
            await RefreshAncestors(activeTreeRoot!,rootChangeVersion,physicalTreeStop.Token);var ancestor=activeTreeRoot!.Parent!;
            await ExpandFolderNode(ancestor);
            if(!ancestor.Children.Any(node=>node.Content is FolderNode item&&item.Path==third))throw new InvalidOperationException("重新展开祖先时，旧索引移除了新出现的兄弟目录。");
            report["ancestorUsesCurrentPhysicalChildren"]=true;
            string shallow=Path.Combine(dataDirectory,"shallow"),nested=Path.Combine(shallow,"nested");Directory.CreateDirectory(nested);await File.WriteAllBytesAsync(Path.Combine(nested,"image.png"),png);
            browseDepth=1;UpdateBrowseDepthLabel();await OpenRoot(shallow);
            if(resultHandle?.Count!=0)throw new InvalidOperationException("不递归时错误显示了嵌套图片。");
            await ApplyBrowseDepth(null);
            if(resultHandle?.Count!=1)throw new InvalidOperationException("切换所有层级后没有显示已扫描的嵌套图片。");
            report["browseDepthReusesScannedChildren"]=true;
            if(viewerPressZoom.Percent!=250||!viewerPressZoom.UsesWholeImage(true)||viewerPressZoom.UsesWholeImage(false))throw new InvalidOperationException("按住放大默认值与模式不匹配。");
            if(SortField.Items.Cast<ComboBoxItem>().Any(item=>Equals(item.Tag,"durationMs")))throw new InvalidOperationException("图片排序仍包含时长。");
            sortDescending=false;UpdateSortDirectionIndicator();if(((RotateTransform)SortDirectionIcon.RenderTransform).Angle!=0)throw new InvalidOperationException("升序指示错误。");
            sortDescending=true;UpdateSortDirectionIndicator();if(((RotateTransform)SortDirectionIcon.RenderTransform).Angle!=180)throw new InvalidOperationException("降序指示错误。");
            foreach(var button in new[]{BackButton,ForwardButton,PreviewFit,PreviewActual,PreviewRotate})if(ToolTipService.GetToolTip(button) is null)throw new InvalidOperationException("浏览按钮缺少悬停说明。");
            report["zoomDefaultsAndSortControls"]=true;
            string pagesDirectory=Path.Combine(dataDirectory,"pages");Directory.CreateDirectory(pagesDirectory);
            using var tiff=new InMemoryRandomAccessStream();var multi=await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId,tiff);
            multi.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,48,96,96,pixels);await multi.GoToNextFrameAsync();
            byte[] green=new byte[pixels.Length];for(int index=0;index<green.Length;index+=4){green[index+1]=240;green[index+3]=255;}
            multi.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,48,96,96,green);await multi.FlushAsync();tiff.Seek(0);
            byte[] tiffBytes=new byte[checked((int)tiff.Size)];await tiff.ReadAsync(tiffBytes.AsBuffer(),(uint)tiffBytes.Length,InputStreamOptions.None);await File.WriteAllBytesAsync(Path.Combine(pagesDirectory,"pages.tiff"),tiffBytes);
            suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
            await OpenRoot(pagesDirectory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
            await SelectPreview((FileRow)results![0]!);
            if(selected?.Kind!="other"||selectedProperties?.PageCount!=2||fitBitmap is not null)throw new InvalidOperationException("多页扫描文件没有进入普通文件路径。");
            report["multipageIsOrdinaryFile"]=true;
            suppressFilters=true;SelectTag(Category,"image");suppressFilters=false;
            await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();await SelectPreview((FileRow)results![0]!);
            viewerGestureSelection=selection;viewerLastPoint=new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2);StartViewerMagnifier();
            await WaitUntil(()=>lensTiles.Count>0,TimeSpan.FromSeconds(10));ResetViewerGesture();
            rotation=1;viewerScaleIntent=ViewerScaleIntent.Custom;viewerCustomPhysicalScale=2.5;ApplyViewerScaleIntent();
            await RestoreImageDevice(Shell.XamlRoot.RasterizationScale,Shell.XamlRoot.RasterizationScale);
            if(imagePage!=0||rotation!=1||viewerScaleIntent!=ViewerScaleIntent.Custom||viewerCustomPhysicalScale!=2.5)throw new InvalidOperationException("图片设备恢复丢失旋转或自定义倍率。");
            report["imageDeviceStateRestored"]=true;ClearResultSelection();
            string displayedId=resultHandle!.Id;long displayedCount=resultHandle.Count;
            for(int iteration=0;iteration<4;iteration++)
            {
                var discarded=await catalog!.CreateSnapshot(CurrentFilter(),epoch,generation,lifetime.Token);await catalog.ReleaseSnapshot(discarded.Id);
            }
            if((await catalog!.ReadPage(displayedId,0)).Count!=displayedCount)throw new InvalidOperationException("未发布的候选查询淘汰了界面仍在显示的结果快照。");
            report["displayedSnapshotLeaseRetained"]=true;
            await WaitUntil(()=>thumbnailWorkCount==0,TimeSpan.FromSeconds(10));
            var normalCache=thumbnailCache;await using var unavailableCache=new ThumbnailCache(Path.Combine(dataDirectory,"unavailable-cache"),new(){MinimumFreeBytes=long.MaxValue});await unavailableCache.Initialize();
            try
            {
                thumbnailCache=unavailableCache;var visibleRow=visible.First(item=>item.Item is not null);visibleRow.Thumbnail=null;await LoadThumbnail(FilesGrid,visibleRow);
                if(visibleRow.Thumbnail is null||visibleRow.ThumbnailError.Length>0)throw new InvalidOperationException("缓存空间保护拒绝写入后，已解码图片没有显示。");
                if(!Status.Text.Contains("缓存"))throw new InvalidOperationException("缓存写入警告未显示。");
            }
            finally{thumbnailCache=normalCache;}
            report["cacheWriteFailureStillDisplaysImage"]=true;
            folderGrouping=new(true);sortDescending=false;await OpenRoot(source);
            if(metadataTask is not null)await metadataTask;await RefreshQuery();
            FilesGrid.ScrollIntoView(results![0],ScrollIntoViewAlignment.Leading);Shell.UpdateLayout();
            await WaitUntil(()=>visible.Count>0&&visible.All(item=>item.Thumbnail is not null),TimeSpan.FromSeconds(10));
            var stableRows=visible.Where(item=>item.Item is not null&&item.RelativePath.StartsWith("A\\",StringComparison.Ordinal)).Select(item=>(Row:item,Container:FilesGrid.ContainerFromItem(item),Thumbnail:item.Thumbnail)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(first,"image-02a.png"),png);await File.WriteAllBytesAsync(Path.Combine(first,"image-08a.png"),png);
            await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);await RefreshQuery(scanPreview:true);await Task.Delay(100);
            if(stableRows.Length<10)throw new InvalidOperationException("未覆盖中部插入之间的可见图片。");
            if(stableRows.Any(item=>!ReferenceEquals(item.Thumbnail,item.Row.Thumbnail)||!ReferenceEquals(item.Container,FilesGrid.ContainerFromItem(item.Row))))throw new InvalidOperationException("两处插入使中间未变化的可见图片被重建。");
            report["separatedInsertionsRetainVisibleRows"]=stableRows.Length;
            var insertionSource=FilesGrid.ItemsSource;
            foreach(string name in new[]{"D","E","F"}){string directory=Path.Combine(source,name);Directory.CreateDirectory(directory);await File.WriteAllBytesAsync(Path.Combine(directory,"image.png"),png);}
            await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);await RefreshQuery(scanPreview:true);
            if(!ReferenceEquals(insertionSource,FilesGrid.ItemsSource))throw new InvalidOperationException("新增多个文件夹组导致重新绑定。");
            var priorGroupSource=FilesGrid.ItemsSource;var groupIdentities=browserGroups!.Cast<BrowserFileGroup>().ToDictionary(group=>group.Info.Id);
            folderGrouping=folderGrouping with{Direction=folderGrouping.Direction=="asc"?"desc":"asc"};await RefreshQuery(scanPreview:true);await Task.Delay(100);
            var ordered=await catalog!.ReadGroups(resultHandle!.Id);
            report["groupOrderBindingRetained"]=ReferenceEquals(FilesGrid.ItemsSource,priorGroupSource);report["groupOrderViewCount"]=FilesGrid.Items.Count;report["groupOrderSnapshotCount"]=resultHandle.Count;
            if(!ReferenceEquals(FilesGrid.ItemsSource,priorGroupSource)||FilesGrid.Items.Count!=resultHandle.Count||!browserGroups!.Cast<BrowserFileGroup>().Select(group=>group.Info).SequenceEqual(ordered))throw new InvalidOperationException("真实分组视图重排后的数量/顺序/绑定不一致。");
            foreach(var group in browserGroups.Cast<BrowserFileGroup>())if(!ReferenceEquals(group,groupIdentities[group.Info.Id]))throw new InvalidOperationException("重排创建了不同的旧组对象。");
            for(int index=0;index<FilesGrid.Items.Count;index++)if(((FileRow)FilesGrid.Items[index]).Ordinal!=index)throw new InvalidOperationException("分组重排后展平顺序错误。");
            report["groupReorderingRetainsSourceAndIdentity"]=true;
        }
        catch(Exception error){report["status"]="FAIL";report["error"]=error.ToString();Environment.ExitCode=1;}
        finally
        {
            report["elapsedMs"]=timer.ElapsedMilliseconds;
            if(Environment.GetCommandLineArgs().Contains("--verify-report-failure"))report["invalidNumber"]=double.NaN;
            try{await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-refresh.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));}
            catch(Exception error){Environment.ExitCode=1;await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-report-error.txt"),error.ToString());}
            finally
            {
                // A diagnostic serialization failure must not leave the offscreen app running.
                PostVerificationClose(WinRT.Interop.WindowNative.GetWindowHandle(this),0x0010,0,0);
            }
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="PostMessageW",SetLastError=true)]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostVerificationClose(nint window,uint message,nuint wParam,nint lParam);
    private static async Task WaitUntil(Func<bool> condition,TimeSpan timeout)
    {
        var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>timeout)throw new TimeoutException("原生列表在限定时间内未就绪。");await Task.Delay(50);}
    }
    private async Task VerifyRetiredRow(string source,Dictionary<string,object> report)
    {
        folderGrouping=new();await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var removed=(FileRow)results![0]!;await results.EnsureLoaded(removed,lifetime.Token);
        File.Delete(Path.Combine(source,removed.RelativePath));
        await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);await RefreshQuery(scanPreview:true);
        var current=visible.ToArray();visible.Clear();visible.Add(removed);foreach(var row in current)visible.Add(row);
        report["injectedRetiredRow"]=true;
        await RefreshQuery(scanPreview:true);
        var expected=(await catalog!.ReadPage(resultHandle!.Id,0))[0];var actual=(FileRow)results![0]!;await results.EnsureLoaded(actual,lifetime.Token);
        report["getterMatchesSnapshot"]=actual.Item!.EntryId==expected.EntryId;
        if(actual.Item.EntryId!=expected.EntryId)throw new InvalidOperationException("残留旧行污染了下一次未变化结果。");
        var handle=await catalog.CreateSnapshot(CurrentFilter(),epoch,generation,lifetime.Token);
        try
        {
            using var next=new VirtualResults(catalog,handle,DispatcherQueue);
            var edits=await catalog.CompareSnapshots(resultHandle,handle,[],[],lifetime.Token);
            RetainBrowserRows(results,next,[removed],[],[],edits,[]);
            report["migrationRejectedRetiredRow"]=!ReferenceEquals(next[0],removed);
            if(ReferenceEquals(next[0],removed))throw new InvalidOperationException("实际迁移函数把不属于上一结果的旧行放入稳定前缀。");
            using var positive=new VirtualResults(catalog,handle,DispatcherQueue);
            RetainBrowserRows(results,positive,[actual],[],[],edits,[]);
            if(!ReferenceEquals(positive[0],actual))throw new InvalidOperationException("迁移函数丢弃了仍属于上一结果的未变化行。");
            report["migrationRetainsCurrentRow"]=true;
        }
        finally{await catalog.ReleaseSnapshot(handle.Id);}
        report["status"]="PASS";
    }
    private async Task VerifyRefreshMetadata(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        MinWidth.Value=32;PendingView.IsChecked=false;await ApplyBrowserFilters();
        if(resultHandle?.Count!=12)throw new InvalidOperationException("元数据刷新反例的初始图片不完整。");
        string previous=resultHandle.Id;long request=queryRequest;var oldMetadata=metadataTask;
        RefreshRoot(this,new RoutedEventArgs());
        await WaitUntil(()=>queryRequest>request&&resultHandle?.Id!=previous,TimeSpan.FromSeconds(10));
        if(metadataTask is not null)await metadataTask;
        report["matchesAfterRefresh"]=resultHandle!.Count;report["metadataTaskReplaced"]=!ReferenceEquals(oldMetadata,metadataTask);
        if(resultHandle.Count!=12)throw new InvalidOperationException("F5后未变化图片从尺寸筛选结果中消失，元数据未自动恢复。");
        report["status"]="PASS";
    }
    private async Task VerifyPreviewClose(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);var row=(FileRow)results![0]!;await results.EnsureLoaded(row,lifetime.Token);
        verifyClosingState=async()=>
        {
            var result=new Dictionary<string,object>();long before=selection;
            try
            {
                await SelectPreview(row);
                result["selectionChangedAfterClosing"]=selection!=before;
                result["selectionTokenCancelled"]=selectionStop.IsCancellationRequested;
                if(selection!=before||!selectionStop.IsCancellationRequested)throw new InvalidOperationException("关闭后预览入口启动了新选择并重新创建未取消的寿命。");
                result["status"]="PASS";
            }
            catch(Exception error){result["status"]="FAIL";result["error"]=error.ToString();Environment.ExitCode=1;}
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-close.json"),JsonSerializer.Serialize(result));
        };
        report["status"]="CLOSE_PENDING";
    }
    private async Task VerifyPendingPreviewClose(string source,bool page,Dictionary<string,object> report)
    {
        if(!page){await OpenRoot(source);if(metadataTask is not null)await metadataTask;}
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Barrier(CancellationToken token){entered.TrySetResult();return Task.Delay(Timeout.Infinite,token);}
        if(page)verifyPageBarrier=Barrier;else verifyPreviewBarrier=Barrier;
        Task pending=page?ImagePage(-1):SelectPreview((FileRow)results![0]!);await entered.Task;
        verifyClosingState=async()=>
        {
            var check=new Dictionary<string,object>();
            try
            {
                if(!pending.IsCompleted||!selectionStop.IsCancellationRequested)throw new InvalidOperationException("关闭时完整预览或翻页任务没有退役。");
                long before=selection;await SelectPreview(new FileRow(0));await ImagePage(1);
                if(selection!=before)throw new InvalidOperationException("关闭后仍允许新预览或翻页。");
                check["status"]="PASS";check["pendingOperationRetired"]=true;check["newOperationsRejected"]=true;
            }
            catch(Exception error){check["status"]="FAIL";check["error"]=error.ToString();Environment.ExitCode=1;}
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-close.json"),JsonSerializer.Serialize(check));
        };
        report["status"]="CLOSE_PENDING";
    }
    private async Task VerifyContainerRecycle(string source,string first,byte[] png,Dictionary<string,object> report)
    {
        for(int index=12;index<512;index++)await File.WriteAllBytesAsync(Path.Combine(first,$"image-{index:D3}.png"),png);
        int recycled=0,nullItems=0;
        void ObserveRecycle(ListViewBase sender,ContainerContentChangingEventArgs args)
        {if(args.InRecycleQueue){recycled++;if(args.Item is null)nullItems++;}}
        FilesGrid.ContainerContentChanging+=ObserveRecycle;
        try
        {
            folderGrouping=new();await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
            await WaitUntil(()=>visible.Any(item=>item.Thumbnail is not null&&FilesGrid.ContainerFromItem(item) is not null),TimeSpan.FromSeconds(20));
            var initial=visible.Where(item=>item.Thumbnail is not null&&FilesGrid.ContainerFromItem(item) is not null).OrderBy(item=>item.Ordinal).ToArray();
            var old=initial[0];
            var last=(FileRow)results![results.Count-1]!;FilesGrid.ScrollIntoView(last,ScrollIntoViewAlignment.Leading);
            report["scrollTargetOrdinal"]=last.Ordinal;report["scrollTargetCount"]=results.Count;
            try{await WaitUntil(()=>initial.Any(item=>FilesGrid.ContainerFromItem(item) is null)&&FilesGrid.ContainerFromItem(last) is not null,TimeSpan.FromSeconds(10));}
            finally
            {
                report["oldHasContainer"]=FilesGrid.ContainerFromItem(old) is not null;report["lastHasContainer"]=FilesGrid.ContainerFromItem(last) is not null;
                report["oldSourcePosition"]=results!.IndexOf(old);report["lastSourcePosition"]=results.IndexOf(last);
                report["gridCount"]=FilesGrid.Items.Count;report["scrollOffset"]=FindScrollViewer(FilesGrid)?.VerticalOffset??-1;
                report["visibleOrdinalMin"]=visible.Select(row=>row.Ordinal).DefaultIfEmpty(-1).Min();report["visibleOrdinalMax"]=visible.Select(row=>row.Ordinal).DefaultIfEmpty(-1).Max();
                report["recycleEvents"]=recycled;report["recycleNullItems"]=nullItems;
            }
            var retired=initial.Where(item=>FilesGrid.ContainerFromItem(item) is null).ToArray();
            report["actuallyRetiredInitialRows"]=retired.Length;report["initialRowsKeptRealizedByWinUI"]=initial.Length-retired.Length;
            if(retired.Any(item=>visible.Contains(item)||item.Thumbnail is not null||thumbnailRequests.ContainsKey(item)))throw new InvalidOperationException("实际已回收的初始容器仍保留图片消费者或资源。");
            old=retired[0];
            report["recycleEvents"]=recycled;report["recycleNullItems"]=nullItems;
            report["offscreenStillVisible"]=visible.Contains(old);report["offscreenThumbnailRetained"]=old.Thumbnail is not null;
            if(recycled==0)throw new InvalidOperationException("未触发实际回收事件。");
            if(visible.Contains(old)||old.Thumbnail is not null||thumbnailRequests.ContainsKey(old))throw new InvalidOperationException("离屏且已回收的图片仍占用可见状态或缩略图资源。");
            var one=new GridViewItem();var two=new GridViewItem();var detail=new ListViewItem();
            BindVisibleContainer(FilesGrid,one,old);BindVisibleContainer(FilesGrid,two,old);BindVisibleContainer(FilesList,detail,old);
            BindVisibleContainer(FilesGrid,one,null);BindVisibleContainer(FilesList,detail,null);
            if(!visible.Contains(old)||visibleConsumerCounts[old]!=1)throw new InvalidOperationException("部分容器回收错误地移除了其他消费者。");
            BindVisibleContainer(FilesGrid,two,last);
            if(visible.Contains(old)||visibleConsumerCounts.ContainsKey(old))throw new InvalidOperationException("容器重新绑定未释放原行。");
            BindVisibleContainer(FilesGrid,two,null);
            report["explicitContainerOwnershipChecks"]=true;
            report["status"]="PASS";
        }
        finally{FilesGrid.ContainerContentChanging-=ObserveRecycle;}
    }
    private async Task VerifyTreeRecovery(string source,Dictionary<string,object> report)
    {
        string receipt=Path.Combine(dataDirectory,"abandoned-listing.txt");
        await using var listing=await FolderChildren.Read(source,TreeListingDirectory,lifetime.Token);
        if(Environment.GetCommandLineArgs().Contains("--verify-tree-abrupt-exit"))
        {
            await File.WriteAllTextAsync(receipt,listing.Version);
            // Exit this isolated test process without closing the listing, as after a crash.
            Environment.Exit(0);
        }
        string old=await File.ReadAllTextAsync(receipt);
        report["abandonedListingRemoved"]=!File.Exists(Path.Combine(TreeListingDirectory,old+".sqlite"));
        report["newListingReadable"]=(await listing.ReadPage(0)).Count>0;
        if(!(bool)report["abandonedListingRemoved"]||!(bool)report["newListingReadable"])throw new InvalidOperationException("同一数据目录重启后未回收异常退出遗留的目录清单。");
        report["status"]="PASS";
    }
    private async Task VerifyPageMode(byte[] pixels,Dictionary<string,object> report)
    {
        string directory=Path.Combine(dataDirectory,"multipage");Directory.CreateDirectory(directory);
        using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,48,96,96,pixels);await encoder.GoToNextFrameAsync();
        var green=new byte[pixels.Length];for(int index=0;index<green.Length;index+=4){green[index+1]=240;green[index+3]=255;}
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,48,96,96,green);await encoder.FlushAsync();stream.Seek(0);
        var bytes=new byte[checked((int)stream.Size)];await stream.ReadAsync(bytes.AsBuffer(),(uint)bytes.Length,InputStreamOptions.None);await File.WriteAllBytesAsync(Path.Combine(directory,"pages.tiff"),bytes);
        suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        if(results?.Count!=1)throw new InvalidOperationException("全部文件未保留多页扫描文件。");
        await SelectPreview((FileRow)results[0]!);
        if(selected?.Kind!="other"||selectedProperties?.PageCount!=2||fitBitmap is not null||FrameTools.Visibility!=Visibility.Collapsed)
            throw new InvalidOperationException("多页扫描文件仍进入了图片翻页预览。");
        var pictures=await catalog!.ReadFirstPage(CurrentFilter() with{Kinds=["image"]});
        if(pictures.Items.Count!=0)throw new InvalidOperationException("图片筛选仍包含多页扫描文件。");
        report["multipageRoutedAsFile"]=true;report["pageCount"]=selectedProperties.PageCount;report["imageFilterExcludesPages"]=true;
        ClearResultSelection();
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET kind='image',page_count=NULL WHERE name='pages.tiff'";return command.ExecuteNonQuery();});
        await RefreshQuery();await SelectPreview((FileRow)results![0]!);
        if(selected?.Kind!="other"||fitBitmap is not null||ImageCanvas.Visibility!=Visibility.Collapsed)
            throw new InvalidOperationException("尚未完成元数据检测的多页文件仍被作为图片显示。");
        report["coldPreviewAlsoRoutesAsFile"]=true;
        report["status"]="PASS";
    }
    private async Task VerifyTreePageOrder(string source,Dictionary<string,object> report)
    {
        for(int index=0;index<300;index++)Directory.CreateDirectory(Path.Combine(source,$"folder-{index:D3}"));
        var listing=await FolderChildren.Read(source,TreeListingDirectory,lifetime.Token);
        var folder=new FolderNode(source,"pages");var node=new TreeViewNode{Content=folder};FolderTree.RootNodes.Add(node);treeListings[node]=new(listing,0,folder);
        var next=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var last=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyTreePageReadBarrier=start=>start==128?next.Task:last.Task;
        try
        {
            var first=ChangeTreePage(node,128);var second=ChangeTreePage(node,256);
            next.SetResult();await first;last.SetResult();await second;
            report["lastRequestedPage"]=treeListings[node].Start;
            if(treeListings[node].Start!=256)throw new InvalidOperationException("先点下一页再点末页，末页请求被先完成的下一页覆盖。");
        }
        finally{next.TrySetResult();last.TrySetResult();verifyTreePageReadBarrier=null;}
        foreach(bool refresh in new[]{false,true})
        {
            next=new(TaskCreationOptions.RunContinuationsAsynchronously);last=new(TaskCreationOptions.RunContinuationsAsynchronously);
            verifyTreePageReadBarrier=start=>start==128?next.Task:last.Task;
            try
            {
                var first=ChangeTreePage(node,128);var second=ChangeTreePage(node,256);
                if(refresh)await LoadTreeListing(node,folder,rootChangeVersion,false,lifetime.Token);
                last.SetResult();await second;next.SetResult();await first;
                if(treeListings[node].Start!=256)throw new InvalidOperationException("倒序完成或替换目录清单使最新翻页请求丢失。");
            }
            finally{next.TrySetResult();last.TrySetResult();verifyTreePageReadBarrier=null;}
        }
        report["reverseCompletionAndListingReplacement"]=true;
        report["status"]="PASS";
    }
    private async Task VerifyThumbnailUpgrade(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        await WaitUntil(()=>thumbnailWorkCount==0,TimeSpan.FromSeconds(20));
        var row=(FileRow)results![0]!;await results.EnsureLoaded(row,lifetime.Token);row.Thumbnail=null;
        for(int slot=0;slot<WorkerResources.Shared.ThumbnailConcurrency;slot++)await thumbnailSlots.WaitAsync();
        Task details=Task.CompletedTask,grid=Task.CompletedTask;
        try{details=LoadThumbnail(FilesList,row);grid=LoadThumbnail(FilesGrid,row);}
        finally{thumbnailSlots.Release(WorkerResources.Shared.ThumbnailConcurrency);}
        await Task.WhenAll(details,grid);
        report["imageConsumerUpgradedDetailsRequest"]=row.Thumbnail is not null;
        if(row.Thumbnail is null)throw new InvalidOperationException("详情请求占用了行请求，随后出现的图片消费者未获得缩略图。");
        report["status"]="PASS";
    }
    private void VerifyGroupOrder(Dictionary<string,object> report)
    {
        const int count=50000;
        using var source=new VirtualResults(catalog!,new("group-order","root",1,1,count,0,0),DispatcherQueue);
        var old=Enumerable.Range(0,count).Select(i=>new SnapshotGroup(i.ToString(),$"folder-{i:D5}",i,1,i,1,"ready","all")).ToArray();
        browserGroups=new(old.Select(info=>new BrowserFileGroup(source,info)));
        var references=browserGroups.Cast<BrowserFileGroup>().ToArray();
        var reverse=old.Reverse().Select((info,index)=>info with{Start=index}).ToArray();
        var changes=old.ToDictionary(info=>info.Id,_=>(IReadOnlyList<RangeEdit>)Array.Empty<RangeEdit>());
        var timer=Stopwatch.StartNew();UpdateBrowserResults(source,reverse,changes);timer.Stop();
        report["reverse50000Ms"]=timer.Elapsed.TotalMilliseconds;
        if(!browserGroups.Cast<BrowserFileGroup>().SequenceEqual(references.Reverse()))throw new InvalidOperationException("文件夹组重排改变了组身份或顺序。");
        timer.Restart();UpdateBrowserResults(source,reverse,changes);timer.Stop();report["unchanged50000Ms"]=timer.Elapsed.TotalMilliseconds;
        if((double)report["reverse50000Ms"]>100)throw new InvalidOperationException("五万文件夹组逆序更新在UI线程超过100ms；本项未计入绑定和布局成本。");
        report["status"]="PASS";
    }
    private void VerifyResultIdentity(Dictionary<string,object> report)
    {
        var errors=new List<string>();
        using(var source=new VirtualResults(catalog!,new("identity", "root",1,1,4352,0,0),DispatcherQueue))
        {
            var row=(FileRow)source[0]!;
            var view=new VirtualRangeCollection<FileRow>(source.Count,i=>(FileRow)source[i]!,source.IndexOf);
            for(int page=1;page<=16;page++)_=source[page*256];
            if(!ReferenceEquals(row,source[0])||view.IndexOf(row)!=0)errors.Add("17页淘汰后，仍被持有的行身份丢失");
            if(source.IndexOf(new FileRow(0))!=-1)errors.Add("外来源相同ordinal被误认为集合成员");
        }
        using(var oldSource=new VirtualResults(catalog!,new("old", "root",1,1,3,0,0),DispatcherQueue))
        using(var nextSource=new VirtualResults(catalog!,new("next", "root",1,2,2,0,0),DispatcherQueue))
        {
            var rows=Enumerable.Range(0,3).Select(i=>(FileRow)oldSource[i]!).ToArray();
            var group=new BrowserFileGroup(oldSource,new("group","A",0,3,0,3,"ready","all"));
            nextSource.Retain(rows[2],0,null);nextSource.Retain(rows[1],1,null);
            if(group.Items.IndexOf(rows[2])!=2)errors.Add("迁移后旧组初始定位错误");
            int notifications=0;
            ((System.Collections.Specialized.INotifyCollectionChanged)group.Items).CollectionChanged+=(_,_)=>
            {
                notifications++;
                for(int i=0;i<group.Items.Count;i++)
                {
                    var value=group.Items[i];int first=Enumerable.Range(0,group.Items.Count).First(j=>ReferenceEquals(group.Items[j],value));
                    if(group.Items.IndexOf(value)!=first)errors.Add($"三行多段通知{notifications}中间态定位错误");
                }
            };
            group.Update(nextSource,new("group","A",0,2,0,2,"ready","all"),[new(0,1,1),new(2,1,0)]);
            if(!ReferenceEquals(group.Items[0],rows[2])||!ReferenceEquals(group.Items[1],rows[1]))errors.Add("最终顺序错误");
            report["rangeNotifications"]=notifications;
        }
        report["identityErrors"]=errors;
        using(var oldSource=new VirtualResults(catalog!,new("late-old","root",1,1,257,0,0),DispatcherQueue))
        using(var nextSource=new VirtualResults(catalog!,new("late-next","root",1,2,258,0,0),DispatcherQueue))
        {
            _=oldSource[0]; // Only the first page exists when migration begins.
            var edits=new Dictionary<string,SnapshotSplice>{{"",new(0,0,1)}};
            RetainBrowserRows(oldSource,nextSource,oldSource.CachedRows().ToArray(),[],[],edits,[]);
            var view=new VirtualRangeCollection<FileRow>(257,i=>(FileRow)oldSource[i]!,oldSource.IndexOf);
            FileRow? late=null;
            view.CollectionChanged+=(_,_)=>late=(FileRow)view[257]!;
            var changes=new Dictionary<string,IReadOnlyList<RangeEdit>>{{"",[new(0,0,1)]}};
            using(oldSource.ShareUnchangedRowsWith(nextSource,VirtualResults.SharedRanges(257,258,[],[],changes)))
                view.UpdateRanges(changes[""],i=>(FileRow)nextSource[i]!,nextSource.IndexOf);
            oldSource.Dispose();
            report["lateUnchangedRowRetained"]=ReferenceEquals(late,view[257]);
            if(!ReferenceEquals(late,view[257]))errors.Add("通知期间首次读取的未变化行，在发布结束后身份改变");
        }
        using(var previous=new VirtualResults(catalog!,new("reverse-old","root",1,1,600,0,0),DispatcherQueue))
        using(var next=new VirtualResults(catalog!,new("reverse-next","root",1,2,600,0,0),DispatcherQueue))
        {
            SnapshotGroup[] oldGroups=[new("a","A",0,300,0,300,"ready","all"),new("b","B",0,300,300,300,"ready","all")];
            SnapshotGroup[] newGroups=[oldGroups[1] with{Start=0},oldGroups[0] with{Start=300}];
            var changes=new Dictionary<string,IReadOnlyList<RangeEdit>>{{"a",[]},{"b",[]}};
            FileRow first;
            using(previous.ShareUnchangedRowsWith(next,VirtualResults.SharedRanges(600,600,oldGroups,newGroups,changes)))
            {
                first=(FileRow)next[556]!; // New source reads an untouched page first.
                if(!ReferenceEquals(first,previous[256])||previous.IndexOf(first)!=256||next.IndexOf(first)!=556)errors.Add("组逆序的新源先读没有共享旧源身份");
                if(next.IndexOf(new FileRow(556))!=-1)errors.Add("身份桥接接受了外来行");
            }
            previous.Dispose();if(!ReferenceEquals(first,next[556]))errors.Add("组逆序迁移在旧源退休后丢失身份");
        }
        using(var previous=new VirtualResults(catalog!,new("changed-old","root",1,1,1,0,0),DispatcherQueue))
        using(var next=new VirtualResults(catalog!,new("changed-next","root",1,2,1,0,0),DispatcherQueue))
        using(previous.ShareUnchangedRowsWith(next,VirtualResults.SharedRanges(1,1,[],[],new Dictionary<string,IReadOnlyList<RangeEdit>>{{"",[new(0,1,1)]}})))
        {
            if(ReferenceEquals(previous[0],next[0]))errors.Add("已变化区间错误复用旧行");
        }
        if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));
        report["status"]="PASS";
    }
    private async Task VerifyAuditFlow(string source,byte[] png,Dictionary<string,object> report)
    {
        var errors=new List<string>();
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        int faults=0;verifyPublishFault=()=>{faults++;throw new InvalidOperationException("Injected publish boundary failure");};
        try{await RefreshQuery();await RefreshQuery();}finally{verifyPublishFault=null;}
        await RefreshQuery();
        long leases=await Task.Run(()=>
        {
            using var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(RuntimeDataDirectory,"catalog","sessions.sqlite")};Mode=ReadOnly");db.Open();
            using var command=db.CreateCommand();command.CommandText="SELECT SUM(active_leases) FROM ResultSessions";return Convert.ToInt64(command.ExecuteScalar());
        });
        report["publicationFaults"]=faults;report["leasesAfterRecovery"]=leases;
        if(faults!=2||leases!=1)errors.Add("两次发布异常后没有恢复为唯一显示租约");
        long previousRequest=queryRequest;closing=true;
        try{await RefreshQuery();}finally{closing=false;}
        if(queryRequest!=previousRequest)errors.Add("关闭开始后仍启动新查询");
        MinSize.Value=10;MaxSize.Value=1;
        try{_=CaptureClosingView();}catch(Exception){errors.Add("未应用的无效筛选阻断关闭会话捕获");}
        finally{MinSize.Value=MaxSize.Value=double.NaN;}
        string excluded=Path.Combine(dataDirectory,"excluded"),child=Path.Combine(excluded,"A");Directory.CreateDirectory(child);await File.WriteAllBytesAsync(Path.Combine(child,"image.png"),png);
        advanced=new FilterSpec{Exclusions=[new("A","skipScan")]};await OpenRoot(excluded);if(metadataTask is not null)await metadataTask;
        await ResetBrowserFilters();
        if(resultHandle?.Count!=1)errors.Add("清除skipScan后未补扫图片");
        advanced=new FilterSpec{Exclusions=[new("A","skipScan")]};await ApplyBrowserFilters();await ResetBrowserFilters();
        if(resultHandle?.Count!=1)errors.Add("已有索引被excluded后，清除规则没有恢复图片");
        long unchangedEpoch=epoch;Search.Text="image";await ApplyBrowserFilters();if(epoch!=unchangedEpoch)errors.Add("纯查询筛选触发了不必要的重扫");
        report["flowErrors"]=errors;
        if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));
        report["status"]="PASS";
    }
    private async Task VerifyClosingWork(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var entered=new TaskCompletionSource();
        Task Barrier(CancellationToken token){entered.TrySetResult();return Task.Delay(Timeout.Infinite,token);}
        bool rootCase=Environment.GetCommandLineArgs().Contains("--verify-r2-close-root");
        if(rootCase)verifyScanBarrier=Barrier;else verifyCandidateBarrier=Barrier;
        Task pending=rootCase?OpenRoot(source,true):RefreshQuery();await entered.Task;long request=queryRequest;
        MinSize.Value=10;MaxSize.Value=1;
        verifyClosingState=async()=>
        {
            var result=new Dictionary<string,object>();
            try
            {
                if(!pending.IsCompleted||queryRequest!=request)throw new InvalidOperationException("关闭后仍有未完成的浏览任务或新查询");
                long leases=await ReadVerificationLeases();if(leases!=0)throw new InvalidOperationException("数据库关闭前仍有结果租约");
                result["status"]="PASS";result["leasesBeforeDatabaseClose"]=leases;result["allWorkRetired"]=true;result["invalidDraftDidNotBlockCleanup"]=true;
            }
            catch(Exception error){result["status"]="FAIL";result["error"]=error.ToString();Environment.ExitCode=1;}
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-close.json"),JsonSerializer.Serialize(result));
        };
        report["status"]="CLOSE_PENDING";
    }
    private Task<long> ReadVerificationLeases()=>Task.Run(()=>
    {
        using var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(RuntimeDataDirectory,"catalog","sessions.sqlite")};Mode=ReadOnly");db.Open();
        using var command=db.CreateCommand();command.CommandText="SELECT COALESCE(SUM(active_leases),0) FROM ResultSessions";return Convert.ToInt64(command.ExecuteScalar());
    });
    private async Task VerifyTreeLimit(Dictionary<string,object> report)
    {
        string source=Path.Combine(dataDirectory,"siblings");
        await Task.Run(()=>{Directory.CreateDirectory(source);for(int index=0;index<2001;index++)Directory.CreateDirectory(Path.Combine(source,$"folder-{index:D5}"));});
        var errors=new List<string>();
        async Task CheckListing(DirectoryListing listing)
        {
            await using(listing)
            {
                if(listing.Count!=2001)errors.Add("目录清单不完整");
                var last=await listing.ReadPage(1920,lifetime.Token);if(last.Count!=81||Path.GetFileName(last[^1])!="folder-02000")errors.Add("末页不可达");
                var first=await listing.ReadPage(0,lifetime.Token);if(first.Count!=128)errors.Add("首页没有受控分页");
                var node=new TreeViewNode{Content=new FolderNode(source,"siblings")};FolderTree.RootNodes.Add(node);
                var state=new TreeListing(listing,0,(FolderNode)node.Content);treeListings[node]=state;ApplyTreePage(node,state,first);
                await ChangeTreePage(node,1920);
                if(node.Children.Count>132||!node.Children.Any(child=>child.Content is FolderNode folder&&Path.GetFileName(folder.Path)=="folder-02000"))errors.Add("原生树末页未显示或节点无界");
                await ChangeTreePage(node,0);if(node.Children.Count>132)errors.Add("返回首页仍保留全部旧节点");
                treeListings.Remove(node);FolderTree.RootNodes.Remove(node);
            }
        }
        try{await CheckListing(await FolderChildren.Read(source,TreeListingDirectory,lifetime.Token));}catch(Exception error){errors.Add("物理: "+error.Message);}
        await catalog!.OpenRoot("tree-limit",source);
        await new DirectoryIndexer(catalog).Scan("tree-limit",source,1,true,[],null,lifetime.Token);
        try{await CheckListing(await catalog.ReadChildDirectories("tree-limit","",source,TreeListingDirectory,lifetime.Token));}catch(Exception error){errors.Add("索引: "+error.Message);}
        report["errors"]=errors;if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));report["status"]="PASS";
    }
    private static void VerifyTreeScale(Dictionary<string,object> report)
    {
        var root=new TreeViewNode();
        var paths=Enumerable.Range(0,5000).Select(index=>Path.Combine(Path.GetTempPath(),"FolderLens-tree-scale",$"folder-{index:D5}")).ToArray();
        MergePhysicalChildren(root,paths);var retained=root.Children.ToArray();
        var timer=Stopwatch.StartNew();MergePhysicalChildren(root,paths);timer.Stop();
        report["unchanged5000Ms"]=timer.Elapsed.TotalMilliseconds;
        if(!root.Children.SequenceEqual(retained))throw new InvalidOperationException("未变化的目录树替换了节点。");
        var expanded=paths.Concat([Path.Combine(Path.GetTempPath(),"FolderLens-tree-scale","folder-05000")]).ToArray();
        timer.Restart();MergePhysicalChildren(root,expanded);timer.Stop();report["append5000Ms"]=timer.Elapsed.TotalMilliseconds;
        if(root.Children.Count!=5001||!root.Children.Take(5000).SequenceEqual(retained))throw new InvalidOperationException("新增兄弟目录未保留原节点。");
        var remaining=expanded.Where((_,index)=>index!=17).ToArray();MergePhysicalChildren(root,remaining);
        if(root.Children.Count!=5000||root.Children.Contains(retained[17])||!root.Children.Take(17).SequenceEqual(retained.Take(17)))throw new InvalidOperationException("删除一个兄弟目录错误影响其他节点。");
        var caseNode=new TreeViewNode();MergePhysicalChildren(caseNode,["sample\\A","sample\\a"]);var distinct=caseNode.Children.ToArray();MergePhysicalChildren(caseNode,["sample\\A","sample\\a"]);
        if(caseNode.Children.Count!=2||!caseNode.Children.SequenceEqual(distinct))throw new InvalidOperationException("大小写不同的真实目录名称被合并。");
        report["removeRetainsSiblings"]=true;report["distinctCaseNamesRetained"]=true;
        if((double)report["unchanged5000Ms"]>100||(double)report["append5000Ms"]>100)throw new InvalidOperationException("5000 个兄弟目录的无变化/单项追加阻塞 UI 超过 100ms。");
        report["status"]="PASS";
    }
}
