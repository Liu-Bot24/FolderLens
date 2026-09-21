using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Action<MediaPlayer>? verifyVideoPlayerCreated;
    private async Task VerifyVideoLifetime(string source,Dictionary<string,object> report)
    {
        var args=Environment.GetCommandLineArgs();int option=Array.IndexOf(args,"--verify-video-lifetime");
        if(option+1>=args.Length||!File.Exists(args[option+1]))throw new InvalidOperationException("Generated video fixture missing.");
        File.Copy(args[option+1],Path.Combine(source,"lifetime.mp4"));
        await File.WriteAllTextAsync(Path.Combine(source,"invalid.mp4"),"Not a video file");
        suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        long ordinal=(await catalog!.FindOrdinal(resultHandle!.Id,"lifetime.mp4"))!.Value;
        await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
        async Task CaptureVideoPreview(string name)
        {
            PreviewPane.UpdateLayout();var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(PreviewPane);
            using var file=File.Create(Path.Combine(dataDirectory,name+".png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        }
        if(VideoPosterPlay.Visibility!=Visibility.Visible)throw new InvalidOperationException("未播放的视频缺少画面播放入口。");
        await CaptureVideoPreview("video-before-play");
        var poster=(IInvokeProvider)new ButtonAutomationPeer(VideoPosterPlay).GetPattern(PatternInterface.Invoke);
        poster.Invoke();
        await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing&&(string)PreviewExternalPlayer.Content=="暂停",TimeSpan.FromSeconds(8));
        if(VideoPosterPlay.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("播放入口仍遮挡视频。");
        SetVideoTransportVisible(false);
        if(videoElement!.TransportControls.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("移出视频后进度控件未隐藏。");
        SetVideoTransportVisible(true);
        if(videoElement.TransportControls.Visibility!=Visibility.Visible)throw new InvalidOperationException("视频进度控件不可用或点击会触发画面播放。");
        PreviewSurface.UpdateLayout();
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);i++)
            {var child=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
        }
        var center=PreviewSurface.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(PreviewSurface.ActualWidth/2,PreviewSurface.ActualHeight/3));
        var hits=Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(center,PreviewSurface).ToArray();
        report["videoSurfaceHitTypes"]=hits.Take(10).Select(element=>element.GetType().Name+":"+(element as FrameworkElement)?.Name).ToArray();
        report["videoSurfaceFilteredAsTransport"]=hits.FirstOrDefault() is {} topHit&&IsVideoTransportTarget(topHit);
        var buttons=Descendants(videoElement.TransportControls).OfType<Microsoft.UI.Xaml.Controls.Primitives.ButtonBase>().ToArray();
        report["transportButtons"]=buttons.Select(button=>button.Name).ToArray();
        var toggle=buttons.First(button=>button.Name=="PlayPauseButtonOnLeft"&&button.ActualWidth>0);
        var togglePeer=FrameworkElementAutomationPeer.CreatePeerForElement(toggle);
        void ToggleNativePlayback()
        {
            if(togglePeer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggleProvider)toggleProvider.Toggle();
            else if(togglePeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invokeProvider)invokeProvider.Invoke();
            else throw new InvalidOperationException("原生播放按钮没有可操作的自动化模式。");
        }
        ToggleNativePlayback();
        await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Paused,TimeSpan.FromSeconds(3));
        report["nativeTransportPauseWorks"]=true;
        ToggleNativePlayback();
        await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing,TimeSpan.FromSeconds(3));
        if(hits.Length==0||IsVideoTransportTarget(hits[0]))throw new InvalidOperationException("视频画面命中被误判成播放条操作。");
        await ToggleVideoFromSource(hits[0]);
        await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Paused&&(string)PreviewExternalPlayer.Content=="播放",TimeSpan.FromSeconds(3));
        if(!IsVideoTransportTarget(toggle))throw new InvalidOperationException("原生按钮点击会重复触发画面切换。");
        await ToggleVideoFromSource(toggle);
        if(videoPlayer!.PlaybackSession.PlaybackState!=MediaPlaybackState.Paused)throw new InvalidOperationException("播放条点击触发了第二次切换。");
        var sliders=Descendants(videoElement.TransportControls).OfType<Microsoft.UI.Xaml.Controls.Slider>().ToArray();
        report["transportSliders"]=sliders.Select(slider=>slider.Name).ToArray();
        var progress=sliders.Single(slider=>slider.Name=="ProgressSlider");
        var range=(IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(progress).GetPattern(PatternInterface.RangeValue);
        double seek=range.Minimum+(range.Maximum-range.Minimum)*.25;range.SetValue(seek);
        await WaitUntil(()=>Math.Abs(videoPlayer.PlaybackSession.Position.TotalSeconds-videoPlayer.PlaybackSession.NaturalDuration.TotalSeconds*.25)<.3,TimeSpan.FromSeconds(3));
        report["nativeProgressSeekWorks"]=true;
        await CaptureVideoPreview("video-paused-hover");
        if(VideoPosterPlay.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("暂停后出现遮挡画面的首次播放入口。");
        var upper=(IInvokeProvider)new ButtonAutomationPeer(PreviewExternalPlayer).GetPattern(PatternInterface.Invoke);
        upper.Invoke();
        await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing&&(string)PreviewExternalPlayer.Content=="暂停",TimeSpan.FromSeconds(3));
        report["posterClickStartsVideo"]=true;report["surfaceClickPausesVideo"]=true;report["upperButtonStateSynchronized"]=true;report["transportOnlyOnHover"]=true;
        StopVideo();
        var retired=new List<WeakReference<MediaPlayer>>();
        verifyVideoPlayerCreated=player=>retired.Add(new(player));
        try
        {
        for(int i=0;i<4;i++)
        {
            await PlayVideoCore();
            if(videoPlayer is null)throw new InvalidOperationException("Video player was not created.");
            await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing&&videoOpenTimer?.IsRunning==false,TimeSpan.FromSeconds(8));
            if(i%2==0)StopVideo();
            else
            {
                // Exercise the real deadline callback's cleanup without waiting
                // fifteen seconds or relying on an unavailable decoder.
                videoOpenTimer!.Stop();videoOpenTimer.Interval=TimeSpan.FromMilliseconds(1);videoOpenTimer.Start();
                await WaitUntil(()=>videoPlayer is null,TimeSpan.FromSeconds(3));
            }
        }
        long invalid=(await catalog.FindOrdinal(resultHandle.Id,"invalid.mp4"))!.Value;
        await SelectBrowserOrdinal(results!,checked((int)invalid),lifetime.Token);
        await PlayVideoCore();
        await WaitUntil(()=>videoPlayer is null,TimeSpan.FromSeconds(17));
        if(retired.Count!=5||!QualityLabel.Text.Contains("无法打开此视频"))throw new InvalidOperationException("Did not exercise the real video failure cleanup.");
        await Task.Delay(1000);
        await Task.Run(()=>{GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,true,true);});
        await Task.Delay(500);
        int alive=retired.Count(reference=>reference.TryGetTarget(out _));
        report["retiredVideoPlayers"]=new{observed=retired.Count,aliveAfterDiagnosticGc=alive};
        if(alive!=0)throw new InvalidOperationException("Disposed video players remain reachable after explicit stop/deadline cleanup.");
        report["status"]="PASS";
        }
        finally{verifyVideoPlayerCreated=null;StopVideo();}
    }
}
