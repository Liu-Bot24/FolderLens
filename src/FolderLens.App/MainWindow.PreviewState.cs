using FolderLens.Infrastructure;
using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool previewLoading;
    private string? previewFailure;
    private Border? loadingBadge;
    private TextBlock? loadingText;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? fitResizeTimer;
    private long previewLayoutRevision;
    private readonly SemaphoreSlim fitResolutionGate=new(1,1);
    private void InitializePreviewState()
    {
        ImageCanvas.CreateResources+=ImageResourcesCreated;
        PreviewSurface.MagnifierResourcesReset+=()=>ResetViewerGesture();
        loadingText=new TextBlock{FontSize=13,Foreground=new SolidColorBrush(Microsoft.UI.Colors.White),TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=400};
        loadingBadge=new Border{Child=loadingText,Background=new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(210,35,35,35)),Padding=new Thickness(12,7,12,7),CornerRadius=new CornerRadius(4),Margin=new Thickness(12),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false,Visibility=Visibility.Collapsed};
        PreviewSurface.Children.Add(loadingBadge);
        fitResizeTimer=DispatcherQueue.CreateTimer();fitResizeTimer.Interval=TimeSpan.FromMilliseconds(180);fitResizeTimer.IsRepeating=false;
        fitResizeTimer.Tick+=async(_,_)=>await RefreshFitAfterResize();
        ImageCanvas.SizeChanged+=(_,_)=>{fitResizeTimer.Stop();fitResizeTimer.Start();};
    }
    private async Task RefreshFitAfterResize()
    {
        if(selected is not {Kind:"image"} row||fitBitmap is null||previewLoading||closing||sourceWidth<=0||sourceHeight<=0||ImageCanvas.ActualWidth<=0||ImageCanvas.ActualHeight<=0)return;
        long current=selection,layout=previewLayoutRevision;var token=selectionStop.Token;
        try
        {
            if(viewerScaleIntent is ViewerScaleIntent.Width or ViewerScaleIntent.Height)
            {
                double before=EffectiveScale();ApplyViewerScaleIntent();
                // Keep the image point at the viewport center while changing the
                // target axis. Clamping only moves it when an edge is reached.
                if(before>0)pan*= (float)(EffectiveScale()/before);
                ClampPan();
            }
            ImageCanvas.Invalidate();UpdateViewerCursor();UpdateViewerInformation();
            if(animationRunning||offlinePreview)return;
            if(zoom>0)await LoadVisibleTiles();else await EnsureFitResolution(row,current,token);
        }
        catch(OperationCanceledException){}catch(Exception ex){if(!closing&&current==selection&&layout==previewLayoutRevision&&!token.IsCancellationRequested)ShowPreviewError(ex);}
    }
    private Task CompleteStaticPreviewPixels(FileRow row,long current,CancellationToken token)
    {
        if(closing||current!=selection||!ReferenceEquals(selected,row)||token.IsCancellationRequested||previewReadySelection!=current
            ||previewLoading||previewFailure is not null||offlinePreview||fitBitmap is null||row.Kind!="image"||zoom<=0
            ||AnimationButton.Visibility==Visibility.Visible||rawPreviewOnly||FileKinds.Raw.Contains(Path.GetExtension(SourcePath(row))))return Task.CompletedTask;
        return LoadVisibleTiles();
    }
    private void PreparePreview()
    {
        previewFailure=null;
        // Keep the current frame (including its detail tiles and transform) until
        // an owned replacement is ready. Non-image content must not inherit it.
        if(selected?.Kind!="image")ClearImage();
        previewLoading=true;UpdateViewerLockToggle();UpdateCommandAvailability();ImageCanvas.Opacity=1;
        if(loadingBadge is not null){loadingText!.Text="正在打开 "+selected?.Name;loadingText.MaxWidth=Math.Max(120,PreviewSurface.ActualWidth-48);loadingBadge.Visibility=Visibility.Visible;}
        ImageCanvas.Invalidate();
    }
    private void ReplaceDisplayedBitmap(Microsoft.Graphics.Canvas.CanvasBitmap bitmap)
    {
        // Transfer the old fit to history only at the synchronous presentation
        // boundary. Cache eviction can no longer blank a pending transition.
        RetainDisplayedFit();
        fitBitmap?.Dispose();fitBitmap=bitmap;
        foreach(var tile in tiles.Values)tile.Dispose();tiles.Clear();
    }
    private void FinishPreview(){previewLoading=false;UpdateViewerLockToggle();UpdateCommandAvailability();ImageCanvas.Opacity=1;if(loadingBadge is not null)loadingBadge.Visibility=previewFailure is null?Visibility.Collapsed:Visibility.Visible;}
    private void ClearResultSelection()
    {
        pendingPreviewRestore=null;previewReadySelection=-1;
        selectionStop.Cancel();prefetchStop.Cancel();selection++;selected=null;selectedProperties=null;
        ResetViewerGesture();slideShow=false;slideTimer?.Stop();animationTimer?.Stop();animationRunning=false;animationRevision++;
        StopVideo();StopAudio();_=ResetTextSession();ClearImage();previewFailure=null;FinishPreview();
        TextContent.Text="";FileTitle.Text="未选择文件";QualityLabel.Text="请选择当前结果中的文件。";
        PreviewCopyFeedback.Visibility=Visibility.Collapsed;
        foreach(var element in new FrameworkElement[]{TextScroll,TextTools,MarkdownHost,AudioTools,FrameTools})element.Visibility=Visibility.Collapsed;
        cloudPreviewButton!.Visibility=Visibility.Collapsed;markdownImages.Clear();UpdateViewerInformation();
    }
    private async Task EnsureFitResolution(FileRow row,long current,CancellationToken cancellation)
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        await fitResolutionGate.WaitAsync(cancellation);
        bool IsCurrent()=>!closing&&current==selection&&ReferenceEquals(selected,row)&&!cancellation.IsCancellationRequested;
        Func<bool> owns=IsCurrent;
        try
        {
        if(!IsCurrent()||offlinePreview)return;
        int requestedRotation=rotation,requestedPage=imagePage;long layout=previewLayoutRevision,device=imageResourceRevision;
        double raster=Shell.XamlRoot.RasterizationScale,canvasWidth=ImageCanvas.ActualWidth,canvasHeight=ImageCanvas.ActualHeight;
        owns=()=>IsCurrent()&&rotation==requestedRotation&&imagePage==requestedPage&&layout==previewLayoutRevision&&device==imageResourceRevision
            &&Shell.XamlRoot.RasterizationScale==raster&&ImageCanvas.ActualWidth==canvasWidth&&ImageCanvas.ActualHeight==canvasHeight&&zoom<=0&&!offlinePreview;
        if(verifyPreviewUpdateBarrier is not null)await verifyPreviewUpdateBarrier("fit",cancellation);
        if(!owns())return;
        if(rawPreviewOnly){await EnsureEmbeddedRawResolution(row,current,cancellation,canPresent:owns);return;}
        if(fitBitmap is null||!animationNeedsOpen||sourceWidth<=0||sourceHeight<=0||zoom>0||row.Kind!="image")return;
        double scale=EffectiveScale()*raster;var actual=fitBitmap.SizeInPixels;
        // Upscaling cannot reveal more detail than the original pixels.
        if(actual.Width+1>=Math.Min(sourceWidth,sourceWidth*scale)&&actual.Height+1>=Math.Min(sourceHeight,sourceHeight*scale))return;
        int width=Math.Max(256,(int)(canvasWidth*raster)),height=Math.Max(256,(int)(canvasHeight*raster));
        if(requestedRotation%2!=0)(width,height)=(height,width);
        verifyFitDecodeRequested?.Invoke();
        var reply=await previewWorker!.RequestPixels(SourcePath(row),"fit",Context(row,current),new(width,height,PageIndex:requestedPage),cancellation,Stamp(row),approvedCloud.Contains(CloudKey(row)));
        try{if(verifyFitResponseBarrier is not null)await verifyFitResponseBarrier(cancellation);}
        catch{await previewWorker.ReleaseAsset(reply);throw;}
        await PresentFit(reply,current,cancellation,owns);
        }
        catch(Exception error) when(!owns()){RecordWebView("Retired fit refresh failed: "+error.GetType().Name);}
        finally{fitResolutionGate.Release();}
    }
}
