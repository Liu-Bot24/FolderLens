using System.Diagnostics;
using System.Text;
using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Media.Playback;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyPreviewCompletion(string source,Dictionary<string,object> report)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text="中文说明\r\n地址 https://example.invalid/";
        await File.WriteAllBytesAsync(Path.Combine(source,"chinese.txt"),Encoding.GetEncoding("gb18030").GetBytes(text));
        await File.WriteAllBytesAsync(Path.Combine(source,"invalid.txt"),[0xff,0xff,0xff]);
        var args=Environment.GetCommandLineArgs();int option=Array.IndexOf(args,"--verify-preview-completion");
        string video=Path.Combine(source,"generated.mp4");File.Copy(args[option+1],video);
        await File.WriteAllBytesAsync(Path.Combine(source,"unsupported.mp4"),[1,2,3,4]);
        suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
        await OpenRoot(source);if(scanTask is not null)await scanTask;await RefreshQuery();
        async Task<FileRow> Row(string name)
        {
            long index=(await catalog!.FindOrdinal(resultHandle!.Id,name))??throw new InvalidOperationException("Missing fixture "+name);
            var row=(FileRow)results![(int)index]!;await results.EnsureLoaded(row,lifetime.Token);return row;
        }
        async Task Choose(string name)
        {
            await WaitUntil(()=>!queryBusy&&!replacingRoot,TimeSpan.FromSeconds(5));
            var row=await Row(name);
            if(!await SelectBrowserOrdinal(results!,(int)row.Ordinal,lifetime.Token))throw new InvalidOperationException("Fixture selection superseded: "+name);
        }
        await Choose("chinese.txt");
        if(TextContent.Text.ReplaceLineEndings("\n")!=text.ReplaceLineEndings("\n")||previewFailure is not null)throw new InvalidOperationException("GB18030 TXT预览仍失败："+QualityLabel.Text);
        await Choose("invalid.txt");
        if(TextContent.Text.Length!=0||previewFailure is null)throw new InvalidOperationException("文本读取失败残留了旧内容，或没有报告错误。");
        await Choose("chinese.txt");
        if(TextContent.Text.ReplaceLineEndings("\n")!=text.ReplaceLineEndings("\n")||previewFailure is not null||loadingBadge!.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("成功文本没有清除旧失败状态。");
        textEncoding="utf-8";await ResetTextSession();
        try{await LoadText(0,selection,selectionStop.Token);throw new InvalidOperationException("显式错误编码未失败。");}
        catch(InvalidDataException error){ShowPreviewError(error);}
        textEncoding="gb18030";await ResetTextSession();await LoadText(0,selection,selectionStop.Token);
        if(previewFailure is not null||loadingBadge.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("同文件切换正确编码后仍残留失败提示。");
        textEncoding=null;
        report["textSuccessFailureSuccess"]=true;
        foreach(bool details in new[]{false,true})
        {
            DetailsMode.IsChecked=details;ToggleView(DetailsMode,new());Shell.UpdateLayout();
            var view=ActiveBrowser;await WaitUntil(()=>visibleContainers.Keys.Count(k=>ReferenceEquals(k.View,view))>=3,TimeSpan.FromSeconds(5));
            var rectangles=visibleContainers.Keys.Where(k=>ReferenceEquals(k.View,view)).Select(k=>(FrameworkElement)k.Container)
                .Select(c=>(Index:view.IndexFromContainer(c),Bounds:c.TransformToVisual(view).TransformBounds(new Rect(0,0,c.ActualWidth,c.ActualHeight))))
                .Where(x=>x.Index>=0&&x.Bounds.Bottom>0&&x.Bounds.Top<view.ActualHeight).OrderBy(x=>x.Index).ToArray();
            if(rectangles.Length<3)throw new InvalidOperationException("框选验证视口不足三行。");
            var first=rectangles[0].Bounds;var second=rectangles[1].Bounds;
            marqueeStart=new(first.Left+1,first.Top+1);marqueeAdditive=false;
            ApplyMarquee(view,new Point(Math.Max(first.Right,second.Right)-1,Math.Max(first.Bottom,second.Bottom)-1));
            if(!view.SelectedRanges.Any(r=>r.FirstIndex<=rectangles[0].Index&&r.FirstIndex+r.Length>rectangles[0].Index)||
               !view.SelectedRanges.Any(r=>r.FirstIndex<=rectangles[1].Index&&r.FirstIndex+r.Length>rectangles[1].Index))throw new InvalidOperationException("两项框选未覆盖真实控件。");
            ApplyMarquee(view,new Point(first.Right-1,first.Bottom-1));
            if(view.SelectedRanges.Sum(r=>(long)r.Length)!=1)throw new InvalidOperationException("缩小选框没有移除范围外选择。");
            marqueeBaseline=[new(rectangles[2].Index,1)];marqueeAdditive=true;ApplyMarquee(view,new Point(first.Right-1,first.Bottom-1));
            if(view.SelectedRanges.Sum(r=>(long)r.Length)!=2)throw new InvalidOperationException("追加框选未保留原选择。");
            marqueeBox!.Visibility=Visibility.Collapsed;report[details?"detailsMarquee":"thumbnailMarquee"]=true;
        }
        await Choose("generated.mp4");if(videoPlayer is not null)throw new InvalidOperationException("选择视频自动创建了播放器。");
        var clock=Stopwatch.StartNew();await PlayVideoCore();
        await WaitUntil(()=>videoPlayer?.PlaybackSession.Position>TimeSpan.FromMilliseconds(300),TimeSpan.FromSeconds(12));
        var player=videoPlayer!;if(player.PlaybackSession.NaturalVideoWidth==0||VideoHost.Visibility!=Visibility.Visible)throw new InvalidOperationException("视频没有产生有效画面尺寸。");
        report["videoFirstPlaybackMs"]=clock.Elapsed.TotalMilliseconds;
        await SetImmersive(true);if(!ReferenceEquals(player,videoPlayer))throw new InvalidOperationException("窗口预览重复创建播放器。");await ReturnToBrowser();
        player.Pause();await Task.Delay(120);double paused=player.PlaybackSession.Position.TotalMilliseconds;await Task.Delay(200);
        if(Math.Abs(player.PlaybackSession.Position.TotalMilliseconds-paused)>120)throw new InvalidOperationException("暂停没有停止播放。");
        player.PlaybackSession.Position=TimeSpan.FromSeconds(2);player.Play();
        await WaitUntil(()=>player.PlaybackSession.Position>TimeSpan.FromSeconds(2.2),TimeSpan.FromSeconds(5));
        await Choose("chinese.txt");
        if(videoPlayer is not null||videoElement is not null||VideoHost.Content is not null||videoOpenTimer is not null)throw new InvalidOperationException("切换文件未释放视频资源。");
        using(var stream=new FileStream(video,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        await Choose("unsupported.mp4");await PlayVideoCore();
        await WaitUntil(()=>videoPlayer is null,TimeSpan.FromSeconds(17));
        if(!QualityLabel.Text.Contains("外部播放"))throw new InvalidOperationException("不支持视频未提供外部播放提示。");
        report["videoPlayPauseSeekSwitchReleaseUnsupported"]=true;report["status"]="PASS";
    }
}
