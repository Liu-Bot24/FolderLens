using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private PressZoomOptions viewerPressZoom=new();
    private bool wholeImageHold;
    private double PressZoomScale=>viewerPressZoom.Percent/100/Shell.XamlRoot.RasterizationScale;
    private async void ConfigureImageViewing(object sender,RoutedEventArgs args)
    {
        ResetViewerGesture();
        var mode=new ComboBox{Header="全屏 / 大窗口中长按左键",ItemsSource=new[]{"整张图片放大（默认）","局部放大镜"},SelectedIndex=viewerPressZoom.LargeViewMode=="lens"?1:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var percent=new NumberBox{Header="长按放大比例（%，100% 为原图像素）",Minimum=25,Maximum=800,Value=viewerPressZoom.Percent,SmallChange=25,LargeChange=100,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        var panel=new StackPanel{Spacing=14,MinWidth=400};panel.Children.Add(mode);panel.Children.Add(percent);
        panel.Children.Add(new TextBlock{Text="两种模式共用此比例。左下角小预览始终使用局部放大镜；松开左键恢复原来的显示。",TextWrapping=TextWrapping.Wrap,MaxWidth=460});
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="图片查看设置",Content=panel,PrimaryButtonText="保存",CloseButtonText="取消"};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        viewerPressZoom=new PressZoomOptions(mode.SelectedIndex==1?"lens":"whole",percent.Value).Normalize();await SaveViewerPreferences();
    }
}
