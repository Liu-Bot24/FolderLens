using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed record SlideshowPreferences(double Seconds=5,bool Repeat=false);
    private SlideshowPreferences slideshowPreferences=new();
    private async void ConfigureSlideshow(object sender,RoutedEventArgs e)
    {
        var interval=new NumberBox{Header="每张图片停留时间（秒）",Minimum=1,Maximum=60,Value=slideshowPreferences.Seconds,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        var repeat=new CheckBox{Content="最后一张之后从头播放",IsChecked=slideshowPreferences.Repeat};
        var panel=new StackPanel{Spacing=14,MinWidth=340};panel.Children.Add(interval);panel.Children.Add(repeat);
        panel.Children.Add(new TextBlock{Text="从当前图片开始，沿当前浏览顺序播放图片，跳过视频和其他文件。Ctrl+Space 暂停或继续，Esc 返回列表并结束。",TextWrapping=TextWrapping.Wrap,MaxWidth=420});
        if(await new ContentDialog{XamlRoot=Shell.XamlRoot,Title="幻灯片设置",Content=panel,PrimaryButtonText="保存",CloseButtonText="取消"}.ShowAsync()!=ContentDialogResult.Primary)return;
        slideshowPreferences=new(double.IsFinite(interval.Value)?Math.Clamp(interval.Value,1,60):5,repeat.IsChecked==true);
        slideTimer!.Interval=TimeSpan.FromSeconds(slideshowPreferences.Seconds);
        if(settings is not null)await settings.Save("slideshow.json",slideshowPreferences);
    }
    private void UpdateSlideshowCommands()
    {
        foreach(var item in viewerActionButtons.Where(item=>item.Action==ViewerAction.Slideshow))item.Button.Content=slideShow?"暂停幻灯片":"播放幻灯片";
        foreach(var item in MainMenu.Items.SelectMany(menu=>menu.Items).OfType<MenuFlyoutItem>().Where(item=>item.Tag as string=="ToggleSlideshow"))item.Text=(slideShow?"暂停幻灯片":"播放幻灯片")+"  Ctrl+Space";
    }
}
