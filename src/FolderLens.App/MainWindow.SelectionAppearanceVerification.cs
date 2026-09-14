using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifySelectionAppearance(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,2,lifetime.Token);
        FilesGrid.UpdateLayout();await Task.Delay(150);
        var item=(GridViewItem)FilesGrid.ContainerFromIndex(2);
        if(!item.IsSelected)throw new InvalidOperationException("选中项未同步到缩略图容器。");
        Search.Focus(FocusState.Programmatic);
        await Task.Delay(150);
        async Task<int> Capture(string name)
        {
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(item);
            byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();int borderPixels=0;
            for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)
            {
                if(x>6&&x<bitmap.PixelWidth-7&&y>6&&y<bitmap.PixelHeight-7)continue;
                int at=(y*bitmap.PixelWidth+x)*4;
                if(pixels[at]>120&&pixels[at]>pixels[at+2]+40&&pixels[at+3]>200)borderPixels++;
            }
            using var file=File.Create(Path.Combine(dataDirectory,name+".png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);await encoder.FlushAsync();
            return borderPixels;
        }
        int unfocused=await Capture("selection-unfocused");
        await SetImmersive(true);await ReturnToBrowser();Search.Focus(FocusState.Programmatic);await Task.Delay(150);
        item=(GridViewItem)FilesGrid.ContainerFromIndex(2);int returned=await Capture("selection-returned");
        report["blueBorderPixels"]=new{unfocused,returned};
        if(!item.IsSelected||Math.Min(unfocused,returned)<200)throw new InvalidOperationException("失去焦点或返回列表后，选中项缺少清晰蓝色边框。");
        DetailsMode.IsChecked=true;ToggleView(DetailsMode,new RoutedEventArgs());FilesList.UpdateLayout();await Task.Delay(100);
        FilesList.SelectedIndex=2;Search.Focus(FocusState.Programmatic);await Task.Delay(100);
        var detail=(ListViewItem)FilesList.ContainerFromIndex(2);
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(detail);
        byte[] detailPixels=(await bitmap.GetPixelsAsync()).ToArray();int blue=0;
        for(int at=0;at<detailPixels.Length;at+=4)if(detailPixels[at]>detailPixels[at+2]+25&&detailPixels[at]>90&&detailPixels[at+3]>200)blue++;
        if(!detail.IsSelected||blue<bitmap.PixelWidth*bitmap.PixelHeight/2)throw new InvalidOperationException("详情列表失焦后缺少清晰的蓝色选中行。");
        report["detailSelectedBluePixels"]=blue;
        using(var file=File.Create(Path.Combine(dataDirectory,"detail-selection.png")))using(var stream=file.AsRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,detailPixels);await encoder.FlushAsync();
        }
        var other=(ListViewItem)FilesList.ContainerFromIndex(1);await bitmap.RenderAsync(other);byte[] plain=(await bitmap.GetPixelsAsync()).ToArray();int unselectedBlue=0;
        for(int at=0;at<plain.Length;at+=4)if(plain[at]>plain[at+2]+25&&plain[at]>90&&plain[at+3]>200)unselectedBlue++;
        if(other.IsSelected||unselectedBlue>bitmap.PixelWidth*bitmap.PixelHeight/10)throw new InvalidOperationException("未选中详情行被误画成蓝色选中态。");
        foreach(var state in new[]{"PointerOverSelected","Selected"})
        {
            VisualStateManager.GoToState(detail,state,false);await bitmap.RenderAsync(detail);var statePixels=(await bitmap.GetPixelsAsync()).ToArray();int stateBlue=0;
            for(int at=0;at<statePixels.Length;at+=4)if(statePixels[at]>statePixels[at+2]+25&&statePixels[at]>90&&statePixels[at+3]>200)stateBlue++;
            if(stateBlue<bitmap.PixelWidth*bitmap.PixelHeight/2)throw new InvalidOperationException("详情选中交互状态丢失蓝色背景："+state);
        }
        report["foregroundContextMenuInteraction"]="NOT_RUN";
        report["status"]="PASS";
    }
}
