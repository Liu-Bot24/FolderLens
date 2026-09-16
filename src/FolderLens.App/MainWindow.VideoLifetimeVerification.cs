using Windows.Media.Playback;

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
        var retired=new List<WeakReference<MediaPlayer>>();
        verifyVideoPlayerCreated=player=>retired.Add(new(player));
        try
        {
        for(int i=0;i<4;i++)
        {
            await PlayVideoCore();
            if(videoPlayer is null)throw new InvalidOperationException("Video player was not created.");
            await WaitUntil(()=>videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing,TimeSpan.FromSeconds(8));
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
