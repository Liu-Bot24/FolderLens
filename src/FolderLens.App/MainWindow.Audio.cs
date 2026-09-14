using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private void InitializeAudio()
    {
        audioTimer=DispatcherQueue.CreateTimer();audioTimer.Interval=TimeSpan.FromMilliseconds(500);
        audioTimer.Tick+=(_,_)=>UpdateAudioControls();
    }
    private void StopAudio()
    {
        var previous=audio;audio=null;audioTimer?.Stop();
        if(previous is not null)
        {
            var source=previous.Source;previous.Pause();previous.Source=null;previous.Dispose();
            (source as IDisposable)?.Dispose();
        }
        if(closing)return;
        audioUpdating=true;
        try{AudioPosition.Value=0;AudioPosition.Maximum=1;AudioPosition.IsEnabled=false;AudioTime.Text="";AudioPlayButton.Content="播放";}
        finally{audioUpdating=false;}
    }
    private void UpdateAudioControls()
    {
        if(closing||audio is not {} player)return;
        var session=player.PlaybackSession;audioUpdating=true;
        try
        {
            AudioPosition.Maximum=Math.Max(1,session.NaturalDuration.TotalSeconds);
            AudioPosition.Value=Math.Clamp(session.Position.TotalSeconds,0,AudioPosition.Maximum);
            AudioPosition.IsEnabled=session.CanSeek;
            ToolTipService.SetToolTip(AudioPosition,session.CanSeek?"拖动跳转播放位置":"此音频当前无法拖动定位");
            static string Time(TimeSpan value)=>$"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
            AudioTime.Text=$"{Time(session.Position)} / {Time(session.NaturalDuration)}";
            AudioPlayButton.Content=session.PlaybackState==MediaPlaybackState.Playing?"暂停":"播放";
        }
        finally{audioUpdating=false;}
    }
    private async void PlayAudio(object sender,RoutedEventArgs e)
    {
        if(selected is not {} row||row.Kind!="audio")return;long current=selection;
        try
        {
            if(!await EnsureCloudRead(row,current)||current!=selection||closing)return;
            if(audio is not {} player)
            {
                player=new MediaPlayer{AutoPlay=false,Volume=AudioVolume.Value,IsMuted=AudioMute.IsChecked==true};
                audio=player;var captured=player;
                void OnCurrent(Action action)=>DispatcherQueue.TryEnqueue(()=>{if(!closing&&current==selection&&ReferenceEquals(audio,captured))action();});
                player.MediaOpened+=(_,_)=>OnCurrent(()=>{ApplyAudioRate();AudioState.Text="";UpdateAudioControls();});
                player.PlaybackSession.PlaybackStateChanged+=(_,_)=>OnCurrent(UpdateAudioControls);
                player.MediaEnded+=(_,_)=>OnCurrent(()=>{AudioState.Text="播放结束。";UpdateAudioControls();});
                player.MediaFailed+=(_,error)=>
                {
                    string message=$"无法播放此音频。请检查音频设备，或用其他播放器打开。";
                    OnCurrent(()=>{StopAudio();AudioState.Text=message;});
                };
                AudioState.Text="正在打开音频…";
                player.Source=MediaSource.CreateFromUri(new Uri(SourcePath(row)));
                ApplyAudioRate();player.Play();audioTimer?.Start();
            }
            else if(player.PlaybackSession.PlaybackState==MediaPlaybackState.Playing)player.Pause();
            else player.Play();
        }
        catch(Exception ex){if(current==selection&&!closing){StopAudio();AudioState.Text=$"无法试听：{UserMessages.Error(ex)} 可使用外部播放器打开。";}}
    }
    private void ApplyAudioRate(){if(audio is not null&&PlaybackRate.SelectedItem is ComboBoxItem item)audio.PlaybackSession.PlaybackRate=double.Parse(item.Tag.ToString()!,System.Globalization.CultureInfo.InvariantCulture);}
    private void ChangeAudioRate(object sender,SelectionChangedEventArgs e)=>ApplyAudioRate();
    private void ChangeAudioVolume(object sender,Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e){if(audio is not null)audio.Volume=e.NewValue;}
    private void ToggleAudioMute(object sender,RoutedEventArgs e){if(audio is not null)audio.IsMuted=AudioMute.IsChecked==true;}
    private void SeekAudio(object sender,Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e){if(!audioUpdating&&audio?.PlaybackSession.CanSeek==true)audio.PlaybackSession.Position=TimeSpan.FromSeconds(e.NewValue);}
}
