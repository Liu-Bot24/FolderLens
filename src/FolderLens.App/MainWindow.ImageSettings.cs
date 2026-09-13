using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private PressZoomOptions viewerPressZoom=new();
    private bool wholeImageHold;
    private ComboBox? viewerPressZoomSelector;
    private bool syncingPressZoomSelector;
    private double PressZoomScale=>viewerPressZoom.Percent/100/Shell.XamlRoot.RasterizationScale;
    private void InitializePressZoomSelector(Panel panel)
    {
        viewerPressZoomSelector=new ComboBox{MinWidth=172};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(viewerPressZoomSelector,"长按放大比例");
        foreach(double percent in new double[]{50,100,150,200,250,300,400,800})
            viewerPressZoomSelector.Items.Add(new ComboBoxItem{Content=$"长按放大：{percent}%",Tag=percent});
        viewerPressZoomSelector.SelectionChanged+=async(_,_)=>
        {
            if(syncingPressZoomSelector||viewerPressZoomSelector.SelectedItem is not ComboBoxItem{Tag:double percent})return;
            ResetViewerGesture();viewerPressZoom=(viewerPressZoom with{Percent=percent}).Normalize();
            UpdateViewerInformation();await SaveViewerPreferences();
        };
        panel.Children.Add(viewerPressZoomSelector);SyncPressZoomSelector();
    }
    private void SyncPressZoomSelector()
    {
        if(viewerPressZoomSelector is null)return;
        viewerPressZoomSelector.Visibility=selected?.Kind=="image"?Visibility.Visible:Visibility.Collapsed;
        if(viewerPressZoomSelector.SelectedItem is ComboBoxItem{Tag:double current}&&current==viewerPressZoom.Percent)return;
        syncingPressZoomSelector=true;
        try
        {
            var item=viewerPressZoomSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>x.Tag is double value&&value==viewerPressZoom.Percent);
            if(item is null){item=new ComboBoxItem{Content=$"长按放大：{viewerPressZoom.Percent}%",Tag=viewerPressZoom.Percent};viewerPressZoomSelector.Items.Add(item);}
            viewerPressZoomSelector.SelectedItem=item;
        }
        finally{syncingPressZoomSelector=false;}
    }
    private async void ConfigureImageViewing(object sender,RoutedEventArgs args)
    {
        ResetViewerGesture();
        var mode=new ComboBox{Header="全屏 / 大窗口中长按左键",ItemsSource=new[]{"整张图片放大（默认）","局部放大镜"},SelectedIndex=viewerPressZoom.LargeViewMode=="lens"?1:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var percent=new NumberBox{Header="长按放大比例（%，100% 为原图像素）",Minimum=25,Maximum=800,Value=viewerPressZoom.Percent,SmallChange=25,LargeChange=100,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        var panel=new StackPanel{Spacing=14,MinWidth=400};panel.Children.Add(mode);panel.Children.Add(percent);
        panel.Children.Add(new TextBlock{Text="两种模式共用此比例。左下角小预览始终使用局部放大镜；松开左键恢复原来的显示。",TextWrapping=TextWrapping.Wrap,MaxWidth=460});
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="图片查看设置",Content=panel,PrimaryButtonText="保存",CloseButtonText="取消"};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        viewerPressZoom=new PressZoomOptions(mode.SelectedIndex==1?"lens":"whole",percent.Value).Normalize();UpdateViewerInformation();await SaveViewerPreferences();
    }
}
