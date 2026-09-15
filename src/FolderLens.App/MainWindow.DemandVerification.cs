using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyDemandLayout(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(scanTask is not null)await scanTask;await RefreshQuery();
        if(metadataTask is {IsCompleted:false})throw new InvalidOperationException("普通浏览仍启动整根内容探测。");
        Shell.UpdateLayout();SetPreviewSplit(.25);Shell.UpdateLayout();double high=PreviewPane.ActualHeight;
        SetPreviewSplit(.75);Shell.UpdateLayout();double low=PreviewPane.ActualHeight;
        if(high<=low+40||TreePane.ActualHeight<=0||PreviewHeightDivider.ActualHeight<5)throw new InvalidOperationException("上下分隔没有改变实际预览高度。");
        if(Grid.GetRow(PreviewPane)!=2||Grid.GetRowSpan(BrowserPane)!=3)throw new InvalidOperationException("侧栏分隔破坏了浏览区跨度。");
        await settings!.Save("desktop.json",new DesktopState(SidebarWidth:360,TreeFraction:.25));SetPreviewSplit(.6);await RestoreDesktop();Shell.UpdateLayout();
        if(Math.Abs(treeFraction-.25)>.001||PreviewColumn.Width.Value!=360)throw new InvalidOperationException("上下/左右布局未恢复。");
        if(results is not {Count:>0})throw new InvalidOperationException("没有生成测试图片。");
        await SelectBrowserOrdinal(results,0,lifetime.Token);await SetImmersive(true);Shell.UpdateLayout();
        if(Grid.GetRowSpan(PreviewPane)!=3||PreviewHeightDivider.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("窗口预览未覆盖分隔条。");
        await SetImmersive(false);Shell.UpdateLayout();
        if(Grid.GetRow(PreviewPane)!=2||PreviewHeightDivider.Visibility!=Visibility.Visible||Math.Abs(treeFraction-.25)>.001)throw new InvalidOperationException("返回浏览后分隔布局未恢复。");
        await ToggleSlideshowCore();UpdateSlideshowCommands();
        if(!slideShow||viewerActionButtons.First(item=>item.Action==ViewerAction.Slideshow).Button.Content as string!="暂停幻灯片")throw new InvalidOperationException("幻灯片未显示暂停动作。");
        await ToggleSlideshowCore();UpdateSlideshowCommands();
        if(slideShow)throw new InvalidOperationException("幻灯片没有暂停。");
        await ReturnToBrowser();
        foreach(var list in new ListViewBase[]{FilesGrid,FilesList})
        {
            var labels=((MenuFlyout)list.ContextFlyout).Items.OfType<MenuFlyoutItem>().Select(item=>item.Text).ToArray();
            if(!labels.Any(label=>label.StartsWith("重命名"))||!labels.Any(label=>label.StartsWith("移动到"))||!labels.Any(label=>label.StartsWith("删除")))throw new InvalidOperationException("两种浏览模式的文件菜单不一致。");
        }
        report["previewLargeHeight"]=high;report["previewSmallHeight"]=low;
        report["layoutRestored"]=true;report["plainBrowseNoBulkProbe"]=true;report["slideshowStartPause"]=true;report["sharedFileCommands"]=true;
        report["scope"]="Offscreen native WinUI layout and commands; no real file deletion dialog, no pointer dragging, no performance equivalence claim.";
        report["status"]="PASS";
    }
}
