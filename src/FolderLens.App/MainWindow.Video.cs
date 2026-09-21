using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private MediaPlayer? videoPlayer;
    private MediaPlayerElement? videoElement;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? videoOpenTimer;
    private bool videoStarting;
    private CancellationTokenSource? mediaCoverStop;
    private long videoPresentationSelection=-1;
    private Action? detachVideoEvents;
    private bool videoPointerInside;

    private void InitializeVideoInteraction()
    {
        VideoHost.AddHandler(UIElement.TappedEvent,new TappedEventHandler(VideoSurfaceTapped),true);
        PreviewSurface.PointerEntered+=(_,_)=>SetVideoTransportVisible(true);
        PreviewSurface.PointerExited+=(_,args)=>
        {
            var point=args.GetCurrentPoint(PreviewSurface).Position;
            if(point.X<0||point.Y<0||point.X>=PreviewSurface.ActualWidth||point.Y>=PreviewSurface.ActualHeight)SetVideoTransportVisible(false);
        };
    }
    private static bool IsVideoTransportTarget(DependencyObject? target)
    {
        for(var current=target;current is not null;current=VisualTreeHelper.GetParent(current))
            if(current is ButtonBase or RangeBase or ComboBox or ListViewBase)return true;
        return false;
    }
    private async void VideoSurfaceTapped(object sender,TappedRoutedEventArgs args)
    {
        if(selected?.Kind!="video"||IsVideoTransportTarget(args.OriginalSource as DependencyObject))return;
        args.Handled=true;
        try{await ToggleVideoFromSource(args.OriginalSource as DependencyObject);}catch(Exception error){if(!closing)ShowError(error);}
    }
    private Task ToggleVideoFromSource(DependencyObject? target)
        =>selected?.Kind=="video"&&!IsVideoTransportTarget(target)?PlayVideoCore():Task.CompletedTask;
    private void SetVideoTransportVisible(bool visible)
    {
        videoPointerInside=visible;
        if(videoElement?.TransportControls is not {} controls)return;
        controls.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        if(visible)controls.Show();else controls.Hide();
    }
    private void UpdateVideoPlaybackUi()
    {
        PreviewExternalPlayer.Content=videoPlayer?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing?"暂停":"播放";
        VideoPosterPlay.Visibility=selected?.Kind=="video"&&videoPlayer is null&&!videoStarting?Visibility.Visible:Visibility.Collapsed;
    }

    private void StopVideo()
    {
        videoStarting=false;videoOpenTimer?.Stop();videoOpenTimer=null;
        var player=videoPlayer;videoPlayer=null;
        var detach=detachVideoEvents;detachVideoEvents=null;detach?.Invoke();
        videoElement?.SetMediaPlayer(null);VideoHost.Content=null;videoElement=null;VideoHost.Visibility=Visibility.Collapsed;
        if(player is not null)
        {
            var source=player.Source;
            try{player.Pause();player.Source=null;}
            finally{player.Dispose();(source as IDisposable)?.Dispose();}
        }
        UpdateVideoPlaybackUi();
    }
    private async void PlayVideo(object sender,RoutedEventArgs e)
    {
        try{await PlayVideoCore();}catch(Exception ex){if(!closing)ShowError(ex);}
    }
    private async Task PlayVideoCore()
    {
        using var work=browserWork.Enter();if(work is null||closing||videoStarting||selected is not {Kind:"video"} row)return;
        long current=selection;videoStarting=true;UpdateVideoPlaybackUi();
        try
        {
            if(!await EnsureCloudRead(row,current)||current!=selection||closing)return;
            if(videoPlayer is {} playing)
            {
                if(playing.PlaybackSession.PlaybackState==MediaPlaybackState.Playing)playing.Pause();else playing.Play();
                return;
            }
            videoPresentationSelection=current;mediaCoverStop?.Cancel();
            var target=await ExternalFileLaunch.Resolve(catalog!,prefetchSourceProbe,SourceRootPath(row),SourceRootId(row),row.Item!.EntryId,row.Item.Version,approvedCloud.Contains(CloudKey(row)),selectionStop.Token);
            if(current!=selection||closing)return;
            StopAudio();
            var player=new MediaPlayer{AutoPlay=false,Volume=.5};videoPlayer=player;
            player.CommandManager.IsEnabled=true; // Native transport controls use this command link.
            videoElement=new MediaPlayerElement{AreTransportControlsEnabled=true,AutoPlay=false};
            videoElement.TransportControls.IsCompact=true;
            videoElement.TransportControls.ShowAndHideAutomatically=false;
            videoElement.SetMediaPlayer(player);VideoHost.Content=videoElement;VideoHost.Visibility=Visibility.Visible;
            SetVideoTransportVisible(videoPointerInside);
            UpdateVideoPlaybackUi();
            bool Owns()=>!closing&&current==selection&&ReferenceEquals(player,videoPlayer);
            void OnCurrent(Action action)=>DispatcherQueue.TryEnqueue(()=>{if(Owns())action();});
            void Failure(string message)
            {
                StopVideo();QualityLabel.Text=message;
                scanLog?.Write("video-error",new{request=current,path=SourcePath(row),message});
            }
            void Opened(MediaPlayer sender,object args)=>OnCurrent(()=>{videoOpenTimer?.Stop();QualityLabel.Text="视频预览";previewFailure=null;if(loadingBadge is not null)loadingBadge.Visibility=Visibility.Collapsed;});
            var playback=player.PlaybackSession;
            void StateChanged(MediaPlaybackSession sender,object args)=>OnCurrent(UpdateVideoPlaybackUi);
            void Failed(MediaPlayer sender,MediaPlayerFailedEventArgs error){string detail=error.Error+": "+error.ErrorMessage;OnCurrent(()=>{scanLog?.Write("video-decoder-error",new{request=current,detail});Failure("内置播放无法打开此视频，可能不支持此格式。请使用外部播放。");});}
            var timer=DispatcherQueue.CreateTimer();videoOpenTimer=timer;timer.Interval=TimeSpan.FromSeconds(15);timer.IsRepeating=false;
            void TimedOut(Microsoft.UI.Dispatching.DispatcherQueueTimer sender,object args){if(Owns())Failure("视频打开超时，请重试或使用外部播放。");}
            detachVideoEvents=()=>{player.MediaOpened-=Opened;playback.PlaybackStateChanged-=StateChanged;player.MediaFailed-=Failed;timer.Tick-=TimedOut;};
            player.MediaOpened+=Opened;playback.PlaybackStateChanged+=StateChanged;player.MediaFailed+=Failed;timer.Tick+=TimedOut;
            verifyVideoPlayerCreated?.Invoke(player);
            QualityLabel.Text="正在打开视频…";previewFailure=null;if(loadingBadge is not null)loadingBadge.Visibility=Visibility.Collapsed;
            // Media Foundation opens/decodes asynchronously; never decode a movie
            // to bitmap frames or create a player for every visible thumbnail.
            player.Source=MediaSource.CreateFromUri(new Uri(target.Path));videoOpenTimer.Start();player.Play();
            scanLog?.Write("video-play",new{request=current,path=SourcePath(row)});
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(current==selection&&!closing){StopVideo();QualityLabel.Text="无法内置播放："+UserMessages.Error(ex)+" 可使用外部播放。";RecordPreviewFailure(ex);}}
        finally{if(current==selection){videoStarting=false;UpdateVideoPlaybackUi();}}
    }
}
