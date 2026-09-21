using FolderLens.Infrastructure;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyAudioOnly(string source,Dictionary<string,object> report)
    {
        var args=Environment.GetCommandLineArgs();int option=Array.IndexOf(args,"--verify-audio-only");
        string folder=Path.Combine(source,"audio-only");Directory.CreateDirectory(folder);
        File.Copy(args[option+1],Path.Combine(folder,"only-audio.mp4"));
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyVideoMetadataBarrier=token=>release.Task.WaitAsync(token);
        try
        {
            suppressFilters=true;try{SelectTag(Category,"all");}finally{suppressFilters=false;}
            await OpenRoot(folder);
            await WaitUntil(()=>visible.Any(r=>r.Name=="only-audio.mp4"&&(r.Kind=="audio"||r.ThumbnailError.Length>0)),TimeSpan.FromSeconds(20));
            var row=visible.Single(r=>r.Name=="only-audio.mp4");
            await WaitUntil(()=>!thumbnailRequests.ContainsKey(row),TimeSpan.FromSeconds(20));
            if(row.Kind!="audio"||row.ThumbnailError.Length>0)throw new InvalidOperationException("仅音频 MP4 被当成缩略图错误："+row.ThumbnailError);
            if(row.AudioOnlyVisibility!=Visibility.Visible||row.FileIconVisibility!=Visibility.Collapsed)throw new InvalidOperationException("缺少仅音频状态标志。");
            var properties=(await catalog!.ReadFileProperties(rootId,row.Item!.EntryId,row.Item.Version))!;
            var checkedRow=new FileRow(0);checkedRow.Fill(row.Item);checkedRow.UpdateProperties(properties);
            if(!checkedRow.IsAudioOnly)throw new InvalidOperationException("从已有元数据恢复时丢失仅音频状态。");
            checkedRow.Fill(row.Item with{Version=row.Item.Version+1});
            if(checkedRow.IsAudioOnly)throw new InvalidOperationException("新版本沿用旧的仅音频结论。");
            checkedRow.UpdateProperties(properties);
            if(checkedRow.IsAudioOnly)throw new InvalidOperationException("旧版本元数据污染新版本。");
            checkedRow.FailThumbnail(new System.IO.InvalidDataException("DecodeFailed"));
            if(checkedRow.ThumbnailErrorVisibility!=Visibility.Visible||checkedRow.AudioOnlyVisibility!=Visibility.Collapsed)throw new InvalidOperationException("真实错误被仅音频状态掩盖。");
            report["audioOnlyClassifiedWithoutThumbnailError"]=true;
            await SelectPreview(row);
            if(previewReadySelection!=selection||previewFailure is not null)throw new InvalidOperationException("仅音频预览失败。");
            var card=(UIElement)FilesGrid.ContainerFromItem(row);var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(card);
            using var imageFile=File.Create(Path.Combine(dataDirectory,"audio-only-card.png"));using var stream=imageFile.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
            report["audioOnlyPreviewReady"]=true;report["status"]="PASS";
        }
        finally{release.TrySetResult();verifyVideoMetadataBarrier=null;}
    }
}