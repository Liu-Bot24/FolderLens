using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private int? browseDepth;
    private void UpdateBrowseDepthLabel()
    {
        BrowseDepthButton.Content=browseDepth is {} levels?$"查看层级：{levels} 层":"查看层级：所有层级";
        foreach(var item in ((MenuFlyout)BrowseDepthButton.Flyout).Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked=Equals(item.Tag,browseDepth?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"all");
    }
    private async void ChooseBrowseDepth(object sender,RoutedEventArgs e)
    {
        try
        {
            string tag=(string)((RadioMenuFlyoutItem)sender).Tag;
            await ApplyBrowseDepth(tag=="all"?null:int.Parse(tag,System.Globalization.CultureInfo.InvariantCulture));
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private async Task ApplyBrowseDepth(int? levels)
    {
        browseDepth=levels;UpdateBrowseDepthLabel();
        // An explicit level choice replaces the directory-capacity "direct files" shortcut.
        if(advanced is not null)advanced=advanced with{ScopeDirectFiles=false};
        if(rootId.Length>0&&activeCollectionId is null)await RefreshQuery();
    }
    private async void CustomBrowseDepth(object sender,RoutedEventArgs e)
    {
        try
        {
            long revision=rootChangeVersion;
            var number=new NumberBox{Header="最多查看几层",Minimum=1,Maximum=32767,Value=browseDepth??3,SmallChange=1,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
            var error=new TextBlock{TextWrapping=TextWrapping.Wrap};
            var content=new StackPanel{Spacing=12};
            content.Children.Add(new TextBlock{Text="当前文件夹算第 1 层。例如选择 3 层，将显示当前文件夹、子文件夹和孙级文件夹直接存放的文件。",TextWrapping=TextWrapping.Wrap});
            content.Children.Add(number);content.Children.Add(error);
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="查看层级",Content=content,PrimaryButtonText="应用",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary};
            dialog.PrimaryButtonClick+=(_,args)=>
            {
                if(!double.IsFinite(number.Value)||number.Value!=Math.Truncate(number.Value)||number.Value<1||number.Value>32767)
                {args.Cancel=true;error.Text="请输入 1 至 32767 的整数。";}
            };
            if(await dialog.ShowAsync()==ContentDialogResult.Primary&&!closing&&revision==rootChangeVersion&&activeCollectionId is null)
                await ApplyBrowseDepth((int)number.Value);
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
}
