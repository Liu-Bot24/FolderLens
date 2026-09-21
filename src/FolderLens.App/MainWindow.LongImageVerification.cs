using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyLongImageWheel(string source,Dictionary<string,object> report)
    {
        string tall=Path.Combine(source,"A","image-00.png");
        using(var stream=new InMemoryRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);byte[] pixels=new byte[64*4800*4];
            for(int i=0;i<pixels.Length;i+=4){pixels[i]=(byte)(i/256%255);pixels[i+1]=120;pixels[i+2]=200;pixels[i+3]=255;}
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,4800,96,96,pixels);await encoder.FlushAsync();stream.Seek(0);
            byte[] bytes=new byte[checked((int)stream.Size)];await stream.ReadAsync(bytes.AsBuffer(),(uint)bytes.Length,InputStreamOptions.None);await File.WriteAllBytesAsync(tall,bytes);
        }
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);Shell.UpdateLayout();
        async Task CheckZoomEntrances(string mode)
        {
            await RunViewerAction(ViewerAction.Fit);
            var fit=(IInvokeProvider)new ButtonAutomationPeer(PreviewFit).GetPattern(PatternInterface.Invoke);
            fit.Invoke();
            await WaitUntil(()=>viewerScaleIntent==ViewerScaleIntent.Width,TimeSpan.FromSeconds(3));
            if(Math.Abs(sourceWidth*EffectiveScale()-ImageCanvas.ActualWidth)>.5||Math.Abs((ImageCanvas.ActualHeight-sourceHeight*EffectiveScale())/2+pan.Y)>.5)
                throw new InvalidOperationException("适应屏幕没有等宽并从图顶开始："+mode);
            fit.Invoke();await WaitUntil(()=>viewerScaleIntent==ViewerScaleIntent.Fit,TimeSpan.FromSeconds(3));
            if(ViewerHasOverflow())throw new InvalidOperationException("第二次适应屏幕没有完整显示："+mode);
            await RunViewerAction(ViewerAction.FitWidth);
            var actual=(IInvokeProvider)new ButtonAutomationPeer(PreviewActual).GetPattern(PatternInterface.Invoke);actual.Invoke();
            await WaitUntil(()=>viewerScaleIntent==ViewerScaleIntent.Custom,TimeSpan.FromSeconds(3));
            if(Math.Abs(EffectiveScale()*Shell.XamlRoot.RasterizationScale-1)>.0001||pan.Length()>.5)
                throw new InvalidOperationException("100%没有按实际像素定位到图中心："+mode);
            await RunViewerAction(ViewerAction.Fit);
            var point=new Windows.Foundation.Point(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight*.25);
            double old=EffectiveScale();double anchor=(point.Y-ImageCanvas.ActualHeight/2-pan.Y)/old;
            await ZoomViewerAt(1/Shell.XamlRoot.RasterizationScale,point);
            if(Math.Abs((point.Y-ImageCanvas.ActualHeight/2-pan.Y)/EffectiveScale()-anchor)>.5)
                throw new InvalidOperationException("点选放大没有保持点击位置："+mode);
        }
        await CheckZoomEntrances("preview");
        await SetImmersive(true);Shell.UpdateLayout();await CheckZoomEntrances("window-viewer");
        await SetImmersive(false);Shell.UpdateLayout();
        report["fitTogglesWholeAndWidthTop"]=true;report["actualCentersImage"]=true;report["pointZoomKeepsAnchor"]=true;
        await RunViewerAction(ViewerAction.FitWidth);
        var before=pan;double scale=EffectiveScale();var row=selected;var position=new Windows.Foundation.Point(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2);
        await ApplyViewerWheel(-120,position,VirtualKeyModifiers.Menu);
        if(selected!=row||EffectiveScale()!=scale||pan.Y>=before.Y||pan.X!=before.X)throw new InvalidOperationException("Alt wheel failed to pan the long image vertically without switching or zooming.");
        report["altWheelPansSameImage"]=true;
        await ApplyViewerWheel(120,position,VirtualKeyModifiers.Control);
        if(selected!=row||EffectiveScale()<=scale)throw new InvalidOperationException("Ctrl wheel no longer zooms.");
        report["controlWheelStillZooms"]=true;
        string prior=wheelBehavior;try{wheelBehavior="next";viewerScaleIntent=ViewerScaleIntent.Default;await ApplyViewerWheel(-120,position,VirtualKeyModifiers.None);}
        finally{wheelBehavior=prior;}
        await WaitUntil(()=>selected?.Ordinal==1&&previewReadySelection==selection,TimeSpan.FromSeconds(10));
        report["plainWheelStillChangesFile"]=true;report["status"]="PASS";
    }
}
