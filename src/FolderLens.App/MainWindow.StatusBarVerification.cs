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
        DetailsMode.IsChecked=false;ToggleView(this,new());ShowPaths.IsChecked=false;PresentationChanged(this,new());
        DetailsMode.IsChecked=true;ToggleView(this,new());
        if(ShowPaths.IsChecked!=true||ShowPaths.IsEnabled)throw new InvalidOperationException("详情视图的路径状态没有同步为已勾选且禁用。");
        detailColumns.Single(c=>c.Field=="path").Visible=false;
        if(ShowPaths.IsChecked!=false||ShowPaths.IsEnabled)throw new InvalidOperationException("隐藏路径列后状态未同步。");
        detailColumns.Single(c=>c.Field=="path").Visible=true;
        DetailsMode.IsChecked=false;ToggleView(this,new());
        if(ShowPaths.IsChecked!=false||!ShowPaths.IsEnabled)throw new InvalidOperationException("切回网格丢失了原来的路径显示偏好。");
        report["pathControlReflectsDetailsColumn"]=true;report["gridPathPreferencePreserved"]=true;
        report["opaqueLightAndDark"]=true;report["gridAndDetailsClipped"]=true;report["status"]="PASS";
    }
}
