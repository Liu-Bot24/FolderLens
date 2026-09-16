using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private async Task VerifyDisplayCompatibility(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectPreview((FileRow)results![0]!);await SetImmersive(true);
        // A completed failure must remain visible even when full-screen chrome is hidden.
        PreparePreview();ShowPreviewError(new InvalidDataException("DecodeFailed"));FinishPreview();
        SetFullScreenChrome(true);
        if(loadingBadge?.Visibility!=Visibility.Visible||loadingText?.Text!=previewFailure)
            throw new InvalidOperationException("全屏预览失败后没有保留可见错误。");
        SetFullScreenChrome(false);PreparePreview();FinishPreview();
        if(loadingBadge?.Visibility!=Visibility.Collapsed||previewFailure is not null)
            throw new InvalidOperationException("新预览没有清除上一次错误。");
        report["previewFailureVisibleAndReset"]=true;
        var handle=WinRT.Interop.WindowNative.GetWindowHandle(this);
        var observations=new List<object>();var scales=new HashSet<double>();
        var originalPosition=AppWindow.Position;var originalSize=AppWindow.Size;
        var originalRow=selected;string originalRoot=root;var originalSource=FilesGrid.ItemsSource;
        try
        {
            if(AppWindow.Presenter is OverlappedPresenter presenter)presenter.Restore();
            var displays=DisplayArea.FindAll();
            for(int index=0;index<displays.Count;index++)
            {
                var display=displays[index];
                var work=display.WorkArea;
                AppWindow.MoveAndResize(new RectInt32(work.X+20,work.Y+20,Math.Min(1100,work.Width-40),Math.Min(750,work.Height-40)));
                await Task.Delay(200);Shell.UpdateLayout();
                await WaitUntil(()=>!previewLoading&&fitBitmap is not null,TimeSpan.FromSeconds(8));
                double raster=Shell.XamlRoot.RasterizationScale;uint dpi=GetDpiForWindow(handle);
                if(Math.Abs(raster-dpi/96d)>.02)throw new InvalidOperationException("窗口 DPI 与 XAML 栅格比例不一致。");
                scales.Add(raster);
                await RunViewerAction(ViewerAction.Actual);
                if(Math.Abs(EffectiveScale()*raster-1)>.0001)throw new InvalidOperationException("100% 未保持一个图像像素对应一个屏幕像素。");
                await RunViewerAction(ViewerAction.Fit);
                double width=sourceWidth*EffectiveScale(),height=sourceHeight*EffectiveScale();
                if(width>ImageCanvas.ActualWidth+.5||height>ImageCanvas.ActualHeight+.5||Math.Min(Math.Abs(width-ImageCanvas.ActualWidth),Math.Abs(height-ImageCanvas.ActualHeight))>.5)
                    throw new InvalidOperationException("适应屏幕没有完整容纳图片并让一边贴合可用区域。");
                if(!ReferenceEquals(selected,originalRow)||root!=originalRoot||!ReferenceEquals(FilesGrid.ItemsSource,originalSource))
                    throw new InvalidOperationException("显示器迁移改变了目录、选中项或结果源。");
                observations.Add(new{dpi,raster,canvasWidth=ImageCanvas.ActualWidth,canvasHeight=ImageCanvas.ActualHeight,actualPixels=true,fitBounds=true});
            }
            report["monitors"]=observations;report["mixedDpiTransitionTested"]=scales.Count>1;
            prefetchStop.Cancel();if(prefetchTask is not null)await prefetchTask;
            var currentBitmap=fitBitmap;
            prefetched.AddFirst(new PrefetchedImage(rootId,epoch,"pressure-fixture",1,new(1,0),1,1,new byte[4096],null!));prefetchBytes+=4096;
            prefetchedDetails.AddFirst(new PrefetchedDetail(rootId,epoch,"pressure-detail-fixture",1,new(1,0),0,0,new byte[4096]));prefetchedDetailBytes+=4096;
            OnPrefetchMemoryPressure();await WaitUntil(()=>Volatile.Read(ref prefetchPressurePending)==0,TimeSpan.FromSeconds(2));
            if(prefetched.Count!=0||prefetchedDetails.Count!=0||prefetchBytes!=0||prefetchedDetailBytes!=0||!ReferenceEquals(fitBitmap,currentBitmap)||!ReferenceEquals(selected,originalRow))
                throw new InvalidOperationException("内存压力处理没有释放预取缓存，或影响了正在显示的图片。");
            report["pressureReleasesOnlyPrefetch"]=true;
            report["status"]="PASS";
        }
        finally{AppWindow.MoveAndResize(new RectInt32(originalPosition.X,originalPosition.Y,originalSize.Width,originalSize.Height));}
    }
}
