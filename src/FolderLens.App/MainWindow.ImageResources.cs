using System.Numerics;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Microsoft.Graphics.Canvas.CanvasDevice? imageResourceDevice;
    private long imageResourceRevision;
    private double previousImageRaster=1;
    private void ImageResourcesCreated(CanvasControl sender,CanvasCreateResourcesEventArgs args)
    {
        imageResourceDevice=sender.Device;
        double raster=Shell.XamlRoot?.RasterizationScale??1,previous=previousImageRaster;previousImageRaster=raster;
        if(closing)return;
        if(args.Reason==CanvasCreateResourcesReason.NewDevice){imageResourceRevision++;args.TrackAsyncAction(RestoreImageDevice(previous,raster).AsAsyncAction());}
        else if(args.Reason==CanvasCreateResourcesReason.DpiChanged)args.TrackAsyncAction(RestoreImageDpi(previous,raster).AsAsyncAction());
    }
    private async Task RestoreImageDevice(double previousRaster,double currentRaster)
    {
        var row=selected;
        long restoreRoot=rootChangeVersion,restoreDevice=imageResourceRevision;
        int page=imagePage,frame=animationFrameIndex,angle=rotation;long loops=animationCompletedLoops;
        bool running=animationRunning,sliding=slideShow,preserve=previewReadySelection==selection&&!previewLoading;
        var intent=viewerScaleIntent;double physical=viewerCustomPhysicalScale;Vector2 offset=pan*(float)(previousRaster/currentRaster);
        ResetViewerGesture();animationTimer?.Stop();slideTimer?.Stop();animationRevision++;
        ClearImage();
        if(row?.Kind=="audio")
        {
            long audioSelection=selection;
            try{await LoadMedia(Path.Combine(root,row.RelativePath),"audio",audioSelection,selectionStop.Token);}
            catch(OperationCanceledException){}
            catch(Exception error){if(audioSelection==selection&&!closing)ShowPreviewError(error);}
            return;
        }
        if(row is null||row.Kind is not ("image" or "video"))return;
        RecordWebView("Image device resources rebuilding");
        // SelectPreview retires the previous selection token before starting a replacement decode.
        var restore=SelectPreview(row);long current=selection;
        try
        {
            await restore;
            if(current!=selection||closing||fitBitmap is null)return;
            if(row.Kind=="image"&&preserve)
            {
                animationTimer?.Stop();animationRunning=false;slideTimer?.Stop();
                if(page>0)
                {
                    var ownedPage=await ImagePage(page);
                    if(ownedPage is null||!ReferenceEquals(row,selected)||restoreRoot!=rootChangeVersion||restoreDevice!=imageResourceRevision)return;
                    current=ownedPage.Value;
                }
                if(current!=selection||closing||fitBitmap is null)return;
                if(AnimationButton.Visibility==Microsoft.UI.Xaml.Visibility.Visible)
                {
                    if(animationIdle is {} active)await active.Task.WaitAsync(selectionStop.Token);
                    if(current!=selection||closing)return;
                    pendingAnimationBitmap?.Dispose();pendingAnimationBitmap=null;
                    animationNextFrame=frame;animationCompletedLoops=loops;animationNeedsOpen=true;animationDeadline=0;animationRunning=true;
                    await AdvanceAnimation(true);
                    if(!running)PauseAnimationForDetail();
                }
                if(current!=selection||closing)return;
                viewerScaleIntent=intent;viewerCustomPhysicalScale=physical;rotation=angle;pan=offset;ApplyViewerScaleIntent();ClampPan();
                if(zoom>0)await LoadVisibleTiles();
                if(sliding&&slideShow&&current==selection)slideTimer?.Start();
            }
            if(current==selection){ImageCanvas.Invalidate();UpdateViewerInformation();RecordWebView("Image device resources restored");}
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(current==selection&&!closing)ShowPreviewError(error);}
    }
    private async Task RestoreImageDpi(double previous,double currentRaster)
    {
        ResetViewerGesture();if(selected is not {} row||fitBitmap is null||previewLoading)return;
        long current=selection;var token=selectionStop.Token;
        pan*= (float)(previous/currentRaster);ApplyViewerSizing();
        try{if(zoom>0)await LoadVisibleTiles();else await EnsureFitResolution(row,current,token);}
        catch(OperationCanceledException){}
        catch(Exception error){if(current==selection&&!closing)ShowPreviewError(error);}
    }
    private bool ReportDeviceLoss(Exception error)
    {
        var device=imageResourceDevice;if(device is null)return false;
        if(!device.IsDeviceLost(error.HResult))return false;
        device.RaiseDeviceLost();return true;
    }
}
