using System.Globalization;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private FileProperties? selectedProperties;
    private bool offlinePreview;
    private Button? cloudPreviewButton;
    private readonly HashSet<string> approvedCloud=[];
    private static string CloudKey(FileRow row)=>row.Item!.EntryId+":"+row.Item.Version;
    private static SourceFileStamp? Stamp(FileRow row)=>row.Item is {} item&&row.ModifiedUtcTicks is {} modified?new(item.Bytes,modified):null;
    private void InitializeProperties()
    {
        cloudPreviewButton=new Button{Content="读取此在线文件…",Visibility=Visibility.Collapsed};cloudPreviewButton.Click+=ReadCloudFile;PreviewExtras.Children.Add(cloudPreviewButton);
        foreach(var list in new ListViewBase[]{FilesGrid,FilesList})
        {
            var menu=new MenuFlyout();void Add(string title,RoutedEventHandler action){var item=new MenuFlyoutItem{Text=title};item.Click+=action;menu.Items.Add(item);}
            Add("收藏所选文件…",CollectSelected);menu.Items.Add(new MenuFlyoutSeparator());
            Add(FileCommandLabels.Properties,ShowProperties);Add(FileCommandLabels.CopyPath,CopyPath);Add(FileCommandLabels.CopyFileReference,CopyFileReference);Add(FileCommandLabels.Reveal,Reveal);Add(FileCommandLabels.ExternalOpen,ExternalOpen);list.ContextFlyout=menu;
            list.RightTapped+=(_,e)=>
            {
                if((e.OriginalSource as FrameworkElement)?.DataContext is not FileRow row)return;
                int index=list.Items.IndexOf(row);
                if(!list.SelectedRanges.Any(range=>index>=range.FirstIndex&&index<(long)range.FirstIndex+range.Length))list.SelectedItem=row;
            };
        }
    }
    private async Task<FileProperties> ResolveRow(FileRow row,string id,CancellationToken token)
    {
        long collectionVersion=collectionChangeVersion;
        var file=await catalog!.ReadFileProperties(SourceRootId(row),row.Item!.EntryId,row.Item.Version,token)??throw new IOException("文件已改变，请刷新当前结果。");
        row.UpdateProperties(file,collectionVersion==collectionChangeVersion);return file;
    }
    private async void ShowProperties(object sender,RoutedEventArgs e)
    {
        if(selected?.Item is null||catalog is null)return;
        try
        {
            var file=await ResolveRow(selected,rootId,selectionStop.Token);selectedProperties=file;var panel=new StackPanel{Spacing=8,MinWidth=460,MaxWidth=680};
            void Row(string name,string? value){if(string.IsNullOrWhiteSpace(value))value="未知";var line=new Grid{ColumnSpacing=16};line.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(110)});line.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});line.Children.Add(new TextBlock{Text=name,Opacity=.65});var text=new TextBlock{Text=value,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};Grid.SetColumn(text,1);line.Children.Add(text);panel.Children.Add(line);}
            string Date(long? ticks,bool utc)=>ticks is {} value?new DateTime(value,utc?DateTimeKind.Utc:DateTimeKind.Unspecified).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture):"未知";
            Row("名称",file.Name);Row("相对路径",file.RelativePath);Row("格式",file.Format);Row("大小",FileRow.FormatBytes(file.LogicalBytes));Row("占用空间",file.AllocatedBytes is {} allocated?FileRow.FormatBytes(allocated):"未知");
            Row("显示尺寸",file.Width is {} w&&file.Height is {} h?$"{w:N0} × {h:N0}":null);Row("编码尺寸",file.EncodedWidth is {} ew&&file.EncodedHeight is {} eh?$"{ew:N0} × {eh:N0}":null);Row("位深",file.BitDepth is {} depth?$"{depth} bit":null);
            Row("修改时间",Date(file.ModifiedUtcTicks,true)+" UTC");Row("创建时间",Date(file.CreatedUtcTicks,true)+" UTC");
            string offset=file.CaptureOffsetMinutes is {} minutes&&file.CaptureWallTicks is {} wall?new DateTimeOffset(new DateTime(wall,DateTimeKind.Unspecified),TimeSpan.FromMinutes(minutes)).ToString("zzz"):"时区未知";
            Row("拍摄时间",file.CaptureWallTicks is not null?Date(file.CaptureWallTicks,false)+" "+offset:"未知");
            if(file.Details is {} details)
            {
                Row("相机",string.Join(' ',new[]{details.CameraMake,details.CameraModel}.Where(v=>!string.IsNullOrEmpty(v)).Distinct()));Row("镜头",details.LensModel);Row("ISO",details.Iso?.ToString());Row("快门",details.ExposureRational??(details.ExposureSeconds is {} seconds?seconds.ToString("0.######")+" s":null));Row("光圈",details.Aperture is {} aperture?$"f/{aperture:0.#}":null);Row("焦距",details.FocalLengthMm is {} focal?$"{focal:0.#} mm":null);
                Row("色彩空间",details.SourceColorSpace);Row("ICC",details.HasIccProfile==true?details.IccDescription??"已嵌入配置文件":details.HasIccProfile==false?"未嵌入":"未知");Row("透明通道",details.HasAlpha==true?"有":details.HasAlpha==false?"无":"未知");
                if(details.Streams.Length>0){Row("轨道",string.Join('\n',details.Streams.Select(stream=>$"#{stream.Index} {stream.Kind} · {stream.Codec}{(stream.IsDefault?" · 默认":"")}{(stream.AttachedPicture?" · 封面":"")}")));}
            }
            if(file.Kind is "audio" or "video"){Row("时长",file.DurationMs is {} ms?TimeSpan.FromMilliseconds(ms).ToString():null);Row("视频编码",file.VideoCodec);Row("音频编码",file.AudioCodec);Row("帧率",file.FrameRateNumerator is {} num&&file.FrameRateDenominator is >0?$"{num/(double)file.FrameRateDenominator:0.###} fps":null);}
            var failures=file.FieldStates.Where(pair=>pair.Value.State is "failed" or "deferredOffline").Select(pair=>$"{pair.Key}: {pair.Value.ErrorCode}").ToArray();if(failures.Length>0)Row("未就绪信息",string.Join('\n',failures));
            await new ContentDialog{XamlRoot=Shell.XamlRoot,Title="文件属性",Content=new ScrollViewer{Content=panel,MaxHeight=620},CloseButtonText="关闭"}.ShowAsync();
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private async void ReadCloudFile(object sender,RoutedEventArgs e)
    {
        if(selected?.Item is null)return;var row=selected;long current=selection;
        try{if(!await EnsureCloudRead(row,current))return;PreparePreview();await RenderSelectedContent(row,current,selectionStop.Token);}catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}finally{if(current==selection)FinishPreview();}
    }
    private async Task<bool> EnsureCloudRead(FileRow row,long current)
    {
        if(row.HydrationState!="placeholder"||approvedCloud.Contains(CloudKey(row)))return current==selection;
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="读取在线文件",Content="云存储提供方可能下载此文件的内容，会使用网络与本地磁盘空间。只读取当前选中的文件。",PrimaryButtonText="读取并预览",CloseButtonText="取消"};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary||current!=selection)return false;
        if(approvedCloud.Count>=256)approvedCloud.Clear();approvedCloud.Add(CloudKey(row));cloudPreviewButton!.Visibility=Visibility.Collapsed;return true;
    }
    private static bool IsSourceUnavailable(Exception error)=>error is System.ComponentModel.Win32Exception win32
        ?win32.NativeErrorCode is 2 or 3 or 21 or 53 or 55 or 64 or 67 or 121 or 1167 or 1231 or 1232 or 2250
        :error is IOException && error is not InvalidDataException && (error.HResult&0xffff) is 2 or 3 or 21 or 53 or 55 or 64 or 67 or 121 or 1167 or 1231 or 1232 or 2250;
    private async Task<bool> ShowOfflineThumbnail(FileRow row,long current,CancellationToken token)
    {
        if(thumbnailCache is null||row.Item is null||row.ModifiedUtcTicks is not {} modified)return false;
        string representation=FileKinds.Raw.Contains(Path.GetExtension(row.RelativePath))?"rawEmbedded":"thumbnail";
        foreach(int edge in new[]{1024,512,256})
        {
            using var lease=await thumbnailCache.TryGet(new(row.Item.EntryId,row.Item.Version,modified,row.Item.Bytes,edge,providerIdentity,representation,SourceSignature:row.SourceSignature),token);if(lease is null)continue;
            using var source=lease.OpenRead();using var random=source.AsRandomAccessStream();var bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,random);if(current!=selection){bitmap.Dispose();return false;}
            fitBitmap?.Dispose();fitBitmap=bitmap;sourceWidth=selectedProperties?.Width??bitmap.SizeInPixels.Width;sourceHeight=selectedProperties?.Height??bitmap.SizeInPixels.Height;zoom=0;pan=System.Numerics.Vector2.Zero;ImageCanvas.Opacity=1;ImageCanvas.Invalidate();offlinePreview=true;QualityLabel.Text="离线缓存缩略图 · 原文件暂不可用";return true;
        }
        return false;
    }
}
