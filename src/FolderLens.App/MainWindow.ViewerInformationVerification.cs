using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyViewerInformation(string directory,Dictionary<string,object> report)
    {
        using var stream=new InMemoryRandomAccessStream();
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,32,24,96,96,new byte[32*24*4]);
        await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string,BitmapTypedValue>
        {
            ["/app1/ifd/{ushort=271}"]=new("Fixture Camera",PropertyType.String),
            ["/app1/ifd/{ushort=272}"]=new("Model 2026",PropertyType.String)
        });
        await encoder.FlushAsync();stream.Seek(0);var bytes=new byte[(int)stream.Size];await stream.ReadAsync(bytes.AsBuffer(),(uint)bytes.Length,InputStreamOptions.None);
        await File.WriteAllBytesAsync(Path.Combine(directory,"A","exif.jpg"),bytes);
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        int ordinal=checked((int)(await catalog!.FindOrdinal(resultHandle!.Id,Path.Combine("A","exif.jpg"),lifetime.Token))!);
        await SelectBrowserOrdinal(results!,ordinal,lifetime.Token);
        report["selectedName"]=selected?.Name??"none";report["details"]=selectedProperties?.Details??new();
        if(selectedProperties?.Details?.CameraMake!="Fixture Camera")throw new InvalidOperationException("真实 JPEG 的 EXIF 未被读取。");
        fullScreen=true;UpdateViewerInformation();
        if(viewerInformation?.Text.Contains("Fixture Camera")!=true||PreviewFilePath.Text!=Path.Combine("A","exif.jpg"))throw new InvalidOperationException("EXIF 或相对路径未进入面板。");
        viewerGroupNotice!.Visibility=Visibility.Collapsed;
        // A native popup can be clamped onto the user's visible desktop even when
        // its owner is offscreen. Exercise the actual lifecycle handlers here;
        // foreground popup interaction remains a separate, unverified check.
        BuildViewerContextMenu();SetViewerMenuNotice(true);await Task.Delay(100);
        if(!viewerMenuOpen||viewerGroupNotice.Visibility!=Visibility.Visible||viewerGroupTimer!.IsRunning)throw new InvalidOperationException("右键菜单未同步保持路径提示。");
        SetViewerMenuNotice(false);await Task.Delay(100);
        if(viewerMenuOpen||viewerGroupNotice.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("菜单关闭后路径仍显示。");
        if(FormatViewerExif(new(){State="ready"}).Contains("Fixture"))throw new InvalidOperationException("无 EXIF 图片混入旧信息。");
        VerifyPreviewCopy(report);
        fullScreen=false;report["jpegExifReadAndDisplayed"]=true;report["relativePath"]=true;report["menuNoticeLifecycleHandlers"]=true;report["foregroundPopupInteraction"]="NOT_RUN";report["status"]="PASS";
    }
}
