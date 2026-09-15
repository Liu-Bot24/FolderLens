using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void StopVideo()
    {
        videoStarting=false;videoOpenTimer?.Stop();videoOpenTimer=null;
        var player=videoPlayer;videoPlayer=null;
        videoElement?.SetMediaPlayer(null);VideoHost.Content=null;videoElement=null;VideoHost.Visibility=Visibility.Collapsed;
        if(player is not null)
        {
            var source=player.Source;
            try{player.Pause();player.Source=null;}
            finally{player.Dispose();(source as IDisposable)?.Dispose();}
        }
        PreviewExternalPlayer.Content="播放";
    }
    private async void PlayVideo(object sender,RoutedEventArgs e)
    {
        try{await PlayVideoCore();}catch(Exception ex){if(!closing)ShowError(ex);}
    }
    private async Task PlayVideoCore()
    {
        using var work=browserWork.Enter();if(work is null||closing||videoStarting||selected is not {Kind:"video"} row)return;
        long current=selection;videoStarting=true;
        try
        {
            if(!await EnsureCloudRead(row,current)||current!=selection||closing)return;
            if(videoPlayer is {} playing)
            {
                if(playing.PlaybackSession.PlaybackState==MediaPlaybackState.Playing)playing.Pause();else playing.Play();
                return;
            }
            var target=await ExternalFileLaunch.Resolve(catalog!,prefetchSourceProbe,SourceRootPath(row),SourceRootId(row),row.Item!.EntryId,row.Item.Version,approvedCloud.Contains(CloudKey(row)),selectionStop.Token);
            if(current!=selection||closing)return;
            StopAudio();
            var player=new MediaPlayer{AutoPlay=false,Volume=.5};videoPlayer=player;
            player.CommandManager.IsEnabled=false;
            videoElement=new MediaPlayerElement{AreTransportControlsEnabled=true,AutoPlay=false};
            videoElement.TransportControls.IsCompact=true;
            videoElement.SetMediaPlayer(player);VideoHost.Content=videoElement;VideoHost.Visibility=Visibility.Visible;
            bool Owns()=>!closing&&current==selection&&ReferenceEquals(player,videoPlayer);
            void OnCurrent(Action action)=>DispatcherQueue.TryEnqueue(()=>{if(Owns())action();});
            void Failure(string message)
            {
                StopVideo();QualityLabel.Text=message;
                scanLog?.Write("video-error",new{request=current,path=SourcePath(row),message});
            }
            player.MediaOpened+=(_,_)=>OnCurrent(()=>{videoOpenTimer?.Stop();QualityLabel.Text="视频预览";previewFailure=null;if(loadingBadge is not null)loadingBadge.Visibility=Visibility.Collapsed;});
            player.PlaybackSession.PlaybackStateChanged+=(_,_)=>OnCurrent(()=>PreviewExternalPlayer.Content=player.PlaybackSession.PlaybackState==MediaPlaybackState.Playing?"暂停":"播放");
            player.MediaFailed+=(_,error)=>{string detail=error.Error+": "+error.ErrorMessage;OnCurrent(()=>{scanLog?.Write("video-decoder-error",new{request=current,detail});Failure("内置播放无法打开此视频，可能不支持此格式。请使用外部播放。");});};
            videoOpenTimer=DispatcherQueue.CreateTimer();videoOpenTimer.Interval=TimeSpan.FromSeconds(15);videoOpenTimer.IsRepeating=false;
            videoOpenTimer.Tick+=(_,_)=>{if(Owns())Failure("视频打开超时，请重试或使用外部播放。");};
            QualityLabel.Text="正在打开视频…";previewFailure=null;if(loadingBadge is not null)loadingBadge.Visibility=Visibility.Collapsed;
            // Media Foundation opens/decodes asynchronously; never decode a movie
            // to bitmap frames or create a player for every visible thumbnail.
            player.Source=MediaSource.CreateFromUri(new Uri(target.Path));videoOpenTimer.Start();player.Play();
            scanLog?.Write("video-play",new{request=current,path=SourcePath(row)});
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(current==selection&&!closing){StopVideo();QualityLabel.Text="无法内置播放："+UserMessages.Error(ex)+" 可使用外部播放。";RecordPreviewFailure(ex);}}
        finally{if(current==selection)videoStarting=false;}
    }
}
