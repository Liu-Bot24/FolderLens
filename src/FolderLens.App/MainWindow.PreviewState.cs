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
    private void InitializePreviewState()
    {
        ImageCanvas.CreateResources+=ImageResourcesCreated;
        PreviewSurface.MagnifierResourcesReset+=()=>ResetViewerGesture();
        loadingText=new TextBlock{FontSize=13,Foreground=new SolidColorBrush(Microsoft.UI.Colors.White),TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=400};
        loadingBadge=new Border{Child=loadingText,Background=new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(210,35,35,35)),Padding=new Thickness(12,7,12,7),CornerRadius=new CornerRadius(4),Margin=new Thickness(12),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false,Visibility=Visibility.Collapsed};
        PreviewSurface.Children.Add(loadingBadge);
        fitResizeTimer=DispatcherQueue.CreateTimer();fitResizeTimer.Interval=TimeSpan.FromMilliseconds(180);fitResizeTimer.IsRepeating=false;
        fitResizeTimer.Tick+=async(_,_)=>{if(selected is null||previewLoading||animationRunning||zoom>0||closing)return;try{await EnsureFitResolution(selected,selection,selectionStop.Token);}catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}};
        ImageCanvas.SizeChanged+=(_,_)=>{fitResizeTimer.Stop();fitResizeTimer.Start();};
    }
    private void PreparePreview()
    {
        previewFailure=null;
        foreach(var bitmap in tiles.Values)bitmap.Dispose();tiles.Clear();previewLoading=true;UpdateViewerLockToggle();UpdateCommandAvailability();ImageCanvas.Opacity=fitBitmap is null?1:.4;
        if(loadingBadge is not null){loadingText!.Text="正在打开 "+selected?.Name;loadingText.MaxWidth=Math.Max(120,PreviewSurface.ActualWidth-48);loadingBadge.Visibility=Visibility.Visible;}
    }
    private void FinishPreview(){previewLoading=false;UpdateViewerLockToggle();UpdateCommandAvailability();ImageCanvas.Opacity=1;if(loadingBadge is not null)loadingBadge.Visibility=previewFailure is null?Visibility.Collapsed:Visibility.Visible;}
    private void ClearResultSelection()
    {
        pendingPreviewRestore=null;previewReadySelection=-1;
        selectionStop.Cancel();prefetchStop.Cancel();selection++;selected=null;selectedProperties=null;
        ResetViewerGesture();slideShow=false;slideTimer?.Stop();animationTimer?.Stop();animationRunning=false;animationRevision++;
        StopAudio();_=ResetTextSession();ClearImage();previewFailure=null;FinishPreview();
        TextContent.Text="";FileTitle.Text="未选择文件";QualityLabel.Text="请选择当前结果中的文件。";
        PreviewCopyFeedback.Visibility=Visibility.Collapsed;
        foreach(var element in new FrameworkElement[]{TextScroll,TextTools,MarkdownHost,AudioTools,FrameTools})element.Visibility=Visibility.Collapsed;
        cloudPreviewButton!.Visibility=Visibility.Collapsed;markdownImages.Clear();UpdateViewerInformation();
    }
    private async Task EnsureFitResolution(FileRow row,long current,CancellationToken cancellation)
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        if(rawPreviewOnly){await EnsureEmbeddedRawResolution(row,current,cancellation);return;}
        if(fitBitmap is null||!animationNeedsOpen||sourceWidth<=0||sourceHeight<=0||zoom>0||row.Kind!="image")return;
        double raster=Shell.XamlRoot.RasterizationScale,scale=EffectiveScale()*raster;var actual=fitBitmap.SizeInPixels;
        // Upscaling cannot reveal more detail than the original pixels.
        if(actual.Width+1>=Math.Min(sourceWidth,sourceWidth*scale)&&actual.Height+1>=Math.Min(sourceHeight,sourceHeight*scale))return;
        int width=Math.Max(256,(int)(ImageCanvas.ActualWidth*raster)),height=Math.Max(256,(int)(ImageCanvas.ActualHeight*raster));
        if(rotation%2!=0)(width,height)=(height,width);int heldRotation=rotation;
        var reply=await previewWorker!.Request(SourcePath(row),"fit",Context(row,current),new(width,height,PageIndex:imagePage),cancellation,Stamp(row));await PresentFit(reply,current,cancellation);if(current==selection){rotation=heldRotation;ImageCanvas.Invalidate();}
    }
}
