using System.Runtime.InteropServices.WindowsRuntime;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<CancellationToken,Task>? verifyVideoMetadataBarrier;
    private Func<CancellationToken,Task>? verifyVideoPropertiesRead;
    private async Task VerifyVideoCard(string source,Dictionary<string,object> report)
    {
        var args=Environment.GetCommandLineArgs();int option=Array.IndexOf(args,"--verify-video-card");
        if(option+1>=args.Length||!File.Exists(args[option+1]))throw new InvalidOperationException("需要自有视频样本。");
        File.Copy(args[option+1],Path.Combine(source,"clip.mp4"));
        suppressFilters=true;try{Category.SelectedIndex=1;}finally{suppressFilters=false;}
        await OpenRoot(source);if(scanTask is not null)await scanTask;await RefreshQuery();
        await WaitUntil(()=>visible.Any(row=>row.Kind=="video"&&row.Thumbnail is not null&&row.DurationText.Length>0),TimeSpan.FromSeconds(20));
        if(metadataTask is not null)await metadataTask;
        var earlyVideo=visible.First(row=>row.Kind=="video");
        object originalSource=FilesGrid.ItemsSource;object? originalContainer=FilesGrid.ContainerFromItem(earlyVideo);var earlyCover=earlyVideo.Thumbnail;
        var known=(await catalog!.ReadFileProperties(rootId,earlyVideo.Item!.EntryId,earlyVideo.Item.Version))!;
        // Exercise a retained/cache-hit cover whose row has not yet received its
        // demanded duration. Cold covers now publish metadata in the same request.
        earlyVideo.UpdateProperties(known with{DurationMs=null});
        if(earlyVideo.DurationText.Length!=0)throw new InvalidOperationException("反例没有形成封面已完成、时长未知的状态。");
        await LoadRowProperties(earlyVideo,refresh:true);
        if(earlyVideo.DurationText.Length==0)throw new InvalidOperationException("封面已完成的可见卡片没有在元数据完成后回填时长。");
        if(!ReferenceEquals(originalSource,FilesGrid.ItemsSource)||!ReferenceEquals(originalContainer,FilesGrid.ContainerFromItem(earlyVideo))||!ReferenceEquals(earlyCover,earlyVideo.Thumbnail))throw new InvalidOperationException("元数据回填替换了原卡片或封面。");
        report["coverBeforeMetadataBackfill"]=true;
        var propertyEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var propertyReleased=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int propertyReads=0;
        verifyVideoPropertiesRead=async token=>{if(++propertyReads==1){propertyEntered.TrySetResult();await propertyReleased.Task.WaitAsync(token);}};
        var ongoingProperties=LoadRowProperties(earlyVideo);
        try
        {
            await propertyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await LoadRowProperties(earlyVideo,refresh:true);
        }
        finally{propertyReleased.TrySetResult();}
        try{await ongoingProperties;}finally{verifyVideoPropertiesRead=null;}
        if(propertyReads!=2||propertyRequests.Contains(earlyVideo)||propertyRefreshPending.Contains(earlyVideo))throw new InvalidOperationException("并发属性回填丢失或未释放请求。");
        report["pendingPropertyRefreshReads"]=propertyReads;
        await WaitUntil(()=>visible.Any(row=>row.Kind=="video"&&row.Thumbnail is not null&&row.DurationText.Length>0),TimeSpan.FromSeconds(20));
        var video=visible.First(row=>row.Kind=="video");
        var picture=visible.First(row=>row.Kind=="image");
        var properties=(await catalog!.ReadFileProperties(rootId,video.Item!.EntryId,video.Item.Version))!;
        if(properties.DurationMs is not >0||picture.VideoBadgeVisibility!=Visibility.Collapsed)throw new InvalidOperationException("真实视频时长或图片类型标记错误。");
        report["decodedVideoDurationMs"]=properties.DurationMs.Value;report["decodedVideoCover"]=true;
        await SelectPreview(video);
        if(previewReadySelection!=selection||previewLoading||fitBitmap is null)
            throw new InvalidOperationException("视频封面加载后未能完成预览及临时文件释放。");
        if(Directory.EnumerateFiles(Path.Combine(RuntimeDataDirectory,"temp","covers")).Any())
            throw new InvalidOperationException("视频封面预览留下临时资产。");
        report["selectedVideoCoverReleased"]=true;
        var panel=new StackPanel{Spacing=12,Padding=new Thickness(16),Background=BrowserPane.Background};
        Grid.SetRowSpan(panel,10);Grid.SetColumnSpan(panel,10);Shell.Children.Add(panel);
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}
        }
        var widths=new[]{100d,144d,240d};
        try
        {
            foreach(double width in widths)
            {
                var line=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};panel.Children.Add(line);
                var row=new FileRow(0);row.Fill(video.Item);row.SetPresentation(width,false);row.Thumbnail=video.Thumbnail;
                var card=new ContentControl{Content=row,ContentTemplate=(DataTemplate)Shell.Resources["Card"]};line.Children.Add(card);
                Shell.UpdateLayout();await Task.Delay(30);
                var image=Children(card).OfType<Image>().Single();var originalImage=image.Source;
                var badge=Children(card).OfType<Border>().Single(border=>ToolTipService.GetToolTip(border) is string tip&&tip.StartsWith("视频 · "));
                var text=Children(badge).OfType<TextBlock>().Single(label=>label.Name=="VideoDurationLabel");
                if(text.Text!="时长未知"||badge.Visibility!=Visibility.Visible)throw new InvalidOperationException("缺失时长没有明确显示未知。");
                row.UpdateProperties(properties);Shell.UpdateLayout();
                if(text.Text!=video.DurationText||!ReferenceEquals(image.Source,originalImage))throw new InvalidOperationException("更新时长替换了封面或没有更新绑定。");
                row.UpdateProperties(properties with{Version=properties.Version+1,DurationMs=99});
                if(text.Text!=video.DurationText)throw new InvalidOperationException("过期版本更新了视频时长。");
                row.UpdateProperties(properties with{DurationMs=0});if(text.Text!="0:00:00")throw new InvalidOperationException("零时长被当成未知。");
                row.UpdateProperties(properties with{DurationMs=long.MaxValue});Shell.UpdateLayout();
                var bounds=badge.TransformToVisual(card).TransformBounds(new Windows.Foundation.Rect(0,0,badge.ActualWidth,badge.ActualHeight));
                if(bounds.X<0||bounds.Right>card.ActualWidth+1)throw new InvalidOperationException("长时长角标超出卡片边界。");
                row.Fill(video.Item with{Version=video.Item.Version+1});
                if(text.Text!="时长未知")throw new InvalidOperationException("新版本沿用了旧时长。");
                row.Fill(video.Item);row.UpdateProperties(properties);
                var failed=new FileRow(1);failed.Fill(video.Item);failed.SetPresentation(width,false);failed.FailThumbnail(new InvalidDataException("DecodeFailed"));
                var failureCard=new ContentControl{Content=failed,ContentTemplate=(DataTemplate)Shell.Resources["Card"]};line.Children.Add(failureCard);
                var imageRow=new FileRow(2);imageRow.Fill(picture.Item!);imageRow.SetPresentation(width,false);imageRow.Thumbnail=picture.Thumbnail;
                line.Children.Add(new ContentControl{Content=imageRow,ContentTemplate=(DataTemplate)Shell.Resources["Card"]});
                Shell.UpdateLayout();
                if(failed.VideoBadgeVisibility!=Visibility.Collapsed||!Children(failureCard).OfType<TextBlock>().Any(label=>label.Text=="视频封面不可用"))throw new InvalidOperationException("封面失败状态不明确。");
            }
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(panel);
            using var file=File.Create(Path.Combine(dataDirectory,"video-cards.png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
            report["widths"]=widths;report["durationBindingAndVersionGuard"]=true;report["imageUnaffected"]=true;report["status"]="PASS";
        }
        finally{Shell.Children.Remove(panel);}
        string batch=Path.Combine(source,"bulk-videos");Directory.CreateDirectory(batch);
        for(int i=0;i<24;i++)File.Copy(args[option+1],Path.Combine(batch,$"clip-{i:D2}.mp4"));
        await OpenRoot(batch);
        await WaitUntil(()=>results?.Count==24,TimeSpan.FromSeconds(20));
        for(int i=0;i<24;i++)
        {
            var row=(FileRow)results![i]!;FilesGrid.ScrollIntoView(row);
            await WaitUntil(()=>row.Thumbnail is not null||row.ThumbnailError.Length>0,TimeSpan.FromSeconds(20));
            if(row.Thumbnail is null)throw new InvalidOperationException($"批量视频封面失败 {i}: {row.ThumbnailError}");
            if(i%4==0)
            {
                await SelectPreview(row);
                if(previewReadySelection!=selection||fitBitmap is null)throw new InvalidOperationException("批量封面期间前台预览失败。");
            }
        }
        report["coldVideoCards"]=24;report["foregroundCoversDuringBatch"]=6;
    }
}
