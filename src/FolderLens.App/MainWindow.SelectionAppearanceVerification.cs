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
        report["status"]="PASS";
    }
}
