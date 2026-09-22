using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFullScreenChrome(Dictionary<string,object> report)
    {
        var originalAppearance=appearance;var originalTheme=Shell.RequestedTheme;
        var originalSelection=selected;bool originalLoading=previewLoading;
        var samples=new List<object>();var errors=new List<string>();
        report["samples"]=samples;report["errors"]=errors;
        // Exercise the real layout transition on the offscreen verification window.
        // Do not change the OS presenter: that would move this window onto a monitor.
        report["offscreenLayoutOnly"]=true;
        selected=new FileRow(0);selected.Fill(new(0,"fullscreen-layout",1,"preview.png","",0,null,"image"));
        previewLoading=true;
        try
        {
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(string style in new[]{"soft","native"})
            {
                ApplyAppearance(new(style));Shell.RequestedTheme=theme;Shell.UpdateLayout();
                CheckRestored($"{theme}-{style}-initial");
                await SetImmersive(true);fullScreen=true;
                SetFullScreenChrome(true);SetFullScreenChrome(true);
                await CheckFullScreen($"{theme}-{style}");
                fullScreen=false;SetFullScreenChrome(false);SetFullScreenChrome(false);
                CheckRestored($"{theme}-{style}-window-preview");
                await SetImmersive(false);CheckRestored($"{theme}-{style}-sidebar");

                // A temporary fullscreen override must not replace ThemeResource values
                // with stale literals when the user later changes appearance.
                await SetImmersive(true);fullScreen=true;SetFullScreenChrome(true);
                string alternate=style=="soft"?"native":"soft";
                ApplyAppearance(new(alternate));
                await CheckFullScreen($"{theme}-{style}-to-{alternate}");
                fullScreen=false;SetFullScreenChrome(false);
                CheckRestored($"{theme}-{alternate}-restored");
                await SetImmersive(false);
                ApplyAppearance(new(style));CheckRestored($"{theme}-{style}-changed-after-exit");
            }
        }
        finally
        {
            fullScreen=false;SetFullScreenChrome(false);await SetImmersive(false);
            ApplyAppearance(originalAppearance);Shell.RequestedTheme=originalTheme;
            selected=originalSelection;previewLoading=originalLoading;UpdateViewerInformation();
        }
        if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));
        report["status"]="PASS";

        void CheckRestored(string name)
        {
            Shell.UpdateLayout();
            var resources=Application.Current.Resources;
            bool restored=PreviewPane.Margin.Equals(resources["AppearancePreviewMargin"])
                &&PreviewPane.Padding.Equals(resources["AppearancePreviewPadding"])
                &&PreviewPane.CornerRadius.Equals(resources["AppearanceSidebarRadius"])
                &&PreviewPane.BorderThickness==new Thickness(0,1,0,0);
            samples.Add(new{name,restored,margin=PreviewPane.Margin.ToString(),padding=PreviewPane.Padding.ToString(),radius=PreviewPane.CornerRadius.ToString()});
            if(!restored)errors.Add(name+": 窗口预览未恢复当前主题的边距/圆角/边框");
        }

        async Task CheckFullScreen(string name)
        {
            Shell.UpdateLayout();await Task.Delay(50);Shell.UpdateLayout();
            var bounds=PreviewSurface.TransformToVisual(Shell).TransformBounds(new(0,0,PreviewSurface.ActualWidth,PreviewSurface.ActualHeight));
            bool fills=Shell.ActualWidth>0&&Shell.ActualHeight>0&&Math.Abs(bounds.Left)<.5&&Math.Abs(bounds.Top)<.5
                &&Math.Abs(bounds.Right-Shell.ActualWidth)<.5&&Math.Abs(bounds.Bottom-Shell.ActualHeight)<.5;
            bool square=PreviewPane.CornerRadius==new CornerRadius(0);
            bool chromeHidden=MainMenu.Visibility==Visibility.Collapsed&&AddressToolbar.Visibility==Visibility.Collapsed
                &&Status.Visibility==Visibility.Collapsed&&PreviewHeader.Visibility==Visibility.Collapsed
                &&PreviewActions.Visibility==Visibility.Collapsed&&PreviewExtras.Visibility==Visibility.Collapsed;
            samples.Add(new{name,fills,square,chromeHidden,left=bounds.Left,top=bounds.Top,rightGap=Shell.ActualWidth-bounds.Right,bottomGap=Shell.ActualHeight-bounds.Bottom});
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Shell);
            byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();int w=bitmap.PixelWidth,h=bitmap.PixelHeight;
            bool darkEdge=true;
            for(int x=0;x<w;x++){CheckPixel(x,0);CheckPixel(x,h-1);}
            for(int y=0;y<h;y++){CheckPixel(0,y);CheckPixel(w-1,y);}
            samples.Add(new{name,edgePixelsMatch=darkEdge,width=w,height=h});
            using var stream=File.Create(Path.Combine(dataDirectory,$"fullscreen-{name}.png"));
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)w,(uint)h,96,96,pixels);await encoder.FlushAsync();
            if(!fills||!square||!chromeHidden||!darkEdge)errors.Add(name+": 全屏画面未贴合四边，或仍有圆角/浅色边缘");

            void CheckPixel(int x,int y)
            {
                int p=(y*w+x)*4;
                if(Math.Abs(pixels[p]-0x2b)>1||Math.Abs(pixels[p+1]-0x24)>1||Math.Abs(pixels[p+2]-0x20)>1||pixels[p+3]!=255)darkEdge=false;
            }
        }
    }
}
