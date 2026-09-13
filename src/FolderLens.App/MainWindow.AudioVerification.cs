using System.Text;
using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyAudioRecovery(string source,Dictionary<string,object> report)
    {
        string path=Path.Combine(source,"audio-recovery.wav");
        await File.WriteAllTextAsync(path,"invalid WAV");
        string previousRoot=root;var previousSelected=selected;long previousSelection=selection;
        var binding=FilesGrid.ItemsSource;
        var row=new FileRow(0);row.Fill(new(0,"audio-recovery",1,Path.GetFileName(path),"",11,null,"audio"));
        root=source;selected=row;selection++;AudioMute.IsChecked=true;
        int heartbeats=0;var heartbeat=DispatcherQueue.CreateTimer();heartbeat.Interval=TimeSpan.FromMilliseconds(20);heartbeat.Tick+=(_,_)=>heartbeats++;heartbeat.Start();
        try
        {
            PlayAudio(this,new RoutedEventArgs());
            await WaitUntil(()=>audio is null&&AudioState.Text.Contains("失败"),TimeSpan.FromSeconds(8));
            if(audioTimer!.IsRunning||AudioPosition.IsEnabled||AudioPlayButton.Content as string!="播放")throw new InvalidOperationException("失败后试听控件未复位。");
            report["nativeMediaFailureRecovered"]=true;

            // Silent PCM keeps this fixture from making noise or changing system devices.
            using(var stream=File.Create(path))using(var writer=new BinaryWriter(stream,Encoding.ASCII))
            {
                const int dataLength=8000*2*5;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+dataLength);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(8000);writer.Write(16000);writer.Write((short)2);writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(dataLength);writer.Write(new byte[dataLength]);
            }
            PlayAudio(this,new RoutedEventArgs());
            await WaitUntil(()=>audio?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing,TimeSpan.FromSeconds(8));
            var playing=audio!;
            if(playing.AudioDevice is not null)throw new InvalidOperationException("试听意外固定到特定音频设备。");
            await WaitUntil(()=>playing.PlaybackSession.Position.TotalMilliseconds>100,TimeSpan.FromSeconds(5));
            StopAudio();
            // Queued native callbacks from the disposed session must leave later selection alone.
            selection++;selected=null;AudioState.Text="新文件";
            await Task.Delay(250);
            if(audio is not null||AudioState.Text!="新文件"||audioTimer.IsRunning)throw new InvalidOperationException("旧音频回调改变了新文件状态。");
            if(!ReferenceEquals(binding,FilesGrid.ItemsSource)||heartbeats<5)throw new InvalidOperationException("试听失败或恢复打断了浏览界面。");
            report["retryPlaysWithSystemDefaultDevice"]=true;
            report["oldCallbacksDoNotChangeNewSelection"]=true;
            report["uiHeartbeats"]=heartbeats;
            report["physicalDeviceUnplugTested"]=false;
        }
        finally{heartbeat.Stop();StopAudio();root=previousRoot;selected=previousSelected;selection=previousSelection;}
    }
}
