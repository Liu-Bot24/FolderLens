using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyBrowserStatusBar(string directory,Dictionary<string,object> report)
    {
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
        foreach(bool details in new[]{false,true})
        {
            Shell.RequestedTheme=theme;DetailsMode.IsChecked=details;ToggleView(this,new());Shell.UpdateLayout();await Task.Delay(40);
            var list=ActiveBrowser;
            if(BrowserStatusBar.Background is not SolidColorBrush{Color.A:255,Opacity:1})throw new InvalidOperationException("状态栏背景仍透明。");
            if(list.Clip is not RectangleGeometry clip||Math.Abs(clip.Rect.Height-list.ActualHeight)>.5)throw new InvalidOperationException("文件列表没有按视口裁剪。");
            var listBounds=list.TransformToVisual(BrowserPane).TransformBounds(new(0,0,list.ActualWidth,list.ActualHeight));
            var statusBounds=BrowserStatusBar.TransformToVisual(BrowserPane).TransformBounds(new(0,0,BrowserStatusBar.ActualWidth,BrowserStatusBar.ActualHeight));
            if(listBounds.Bottom>statusBounds.Top+1)throw new InvalidOperationException("列表布局仍覆盖状态栏。");
        }
        report["opaqueLightAndDark"]=true;report["gridAndDetailsClipped"]=true;report["status"]="PASS";
    }
}
