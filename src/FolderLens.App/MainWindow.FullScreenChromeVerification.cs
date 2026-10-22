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
            foreach(string style in new[]{"soft","native","glass"})
            {
                ApplyAppearance(new(style));Shell.RequestedTheme=theme;Shell.UpdateLayout();
                CheckRestored($"{theme}-{style}-initial");
                await SetImmersive(true);fullScreen=true;
                SetFullScreenChrome(true);SetFullScreenChrome(true);
                await CheckFullScreen($"{theme}-{style}");
                await CheckPanelZones($"{theme}-{style}");
                fullScreen=false;SetFullScreenChrome(false);SetFullScreenChrome(false);
                CheckRestored($"{theme}-{style}-window-preview");
                await SetImmersive(false);CheckRestored($"{theme}-{style}-sidebar");

                // A temporary fullscreen override must not replace ThemeResource values
                // with stale literals when the user later changes appearance.
                await SetImmersive(true);fullScreen=true;SetFullScreenChrome(true);
                string alternate=style switch{"soft"=>"native","native"=>"glass",_=>"soft"};
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
                &&PreviewPane.BorderThickness.Equals(resources["AppearancePreviewBorder"]);
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
            bool chromeHidden=AppTitleBar.Visibility==Visibility.Collapsed&&MainMenu.Visibility==Visibility.Collapsed&&AddressToolbar.Visibility==Visibility.Collapsed
                &&Status.Visibility==Visibility.Collapsed&&PreviewHeader.Visibility==Visibility.Collapsed
                &&PreviewActions.Visibility==Visibility.Collapsed&&PreviewExtras.Visibility==Visibility.Collapsed;
            samples.Add(new{name,fills,square,chromeHidden,left=bounds.Left,top=bounds.Top,rightGap=Shell.ActualWidth-bounds.Right,bottomGap=Shell.ActualHeight-bounds.Bottom});
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Shell);
            byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();int w=bitmap.PixelWidth,h=bitmap.PixelHeight;
            var surface=((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["ViewerSurfaceBrush"]).Color;
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
                if(Math.Abs(pixels[p]-surface.B)>1||Math.Abs(pixels[p+1]-surface.G)>1||Math.Abs(pixels[p+2]-surface.R)>1||pixels[p+3]!=255)darkEdge=false;
            }
        }

        // Hover zones must cover a panel's inset as well as the panel itself:
        // a point on the panel keeps it open, a point clearly past it does not.
        async Task CheckPanelZones(string name)
        {
            var panels=new[]{viewerTop!,viewerBottom!,viewerLeft!,viewerRight!};
            foreach(var panel in panels)panel.Visibility=Visibility.Visible;
            Shell.UpdateLayout();
            double width=Shell.ActualWidth,height=Shell.ActualHeight,midY=height/2;
            var t=viewerTop!;var b=viewerBottom!;var l=viewerLeft!;var r=viewerRight!;
            double topEdge=t.Margin.Top+t.ActualHeight-1,bottomEdge=height-b.Margin.Bottom-b.ActualHeight+1;
            double leftEdge=l.Margin.Left+l.ActualWidth-1,rightEdge=width-r.Margin.Right-r.ActualWidth+1;
            var inside=new[]{ViewerPanelZones(new(width/2,topEdge)).Top,ViewerPanelZones(new(width/2,bottomEdge)).Bottom,ViewerPanelZones(new(leftEdge,midY)).Left,ViewerPanelZones(new(rightEdge,midY)).Right};
            var outside=new[]{ViewerPanelZones(new(width/2,topEdge+24)).Top,ViewerPanelZones(new(width/2,bottomEdge-24)).Bottom,ViewerPanelZones(new(leftEdge+24,midY)).Left,ViewerPanelZones(new(rightEdge-24,midY)).Right};
            var probe=new Windows.Foundation.Point(width/2,t.Margin.Top+t.ActualHeight/2);
            foreach(var panel in panels)panel.Visibility=Visibility.Collapsed;
            Shell.UpdateLayout();
            bool zones=inside.All(value=>value)&&!outside.Any(value=>value);
            // Any hide transition must finish and leave the photo surface uncovered.
            await Task.Delay(400);
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Shell);
            byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();double scale=bitmap.PixelWidth/width;
            int at=((int)(probe.Y*scale)*bitmap.PixelWidth+(int)(probe.X*scale))*4;
            var surface=((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["ViewerSurfaceBrush"]).Color;
            bool hidden=Math.Abs(pixels[at]-surface.B)<=2&&Math.Abs(pixels[at+1]-surface.G)<=2&&Math.Abs(pixels[at+2]-surface.R)<=2;
            samples.Add(new{name=name+"-zones",zones,inside,outside,inset=t.Margin.Top,hiddenAfterTransition=hidden});
            if(!zones)errors.Add(name+": 浮层悬停范围没有覆盖浮层（含边距）或超出浮层");
            if(!hidden)errors.Add(name+": 浮层收起后仍遮挡画面");
        }
    }
}
