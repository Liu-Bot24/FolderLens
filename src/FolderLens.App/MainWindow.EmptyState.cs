using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string? browserEmptyError;
    private string? browserScanError;
    private void ShowScanError(Exception error)
    {
        browserScanError=error.Message;reconcilePending=false;ShowBrowserError(error);
    }
    private void InitializeBrowserEmptyState()
    {
        ResultSummary.RegisterPropertyChangedCallback(TextBlock.TextProperty,(_,_)=>UpdateBrowserEmptyState());
        Status.RegisterPropertyChangedCallback(TextBlock.TextProperty,(_,_)=>UpdateBrowserEmptyState());
        UpdateBrowserEmptyState();
    }
    private void ShowBrowserError(Exception error)
    {
        browserEmptyError=error.Message;ResultSummary.Text="浏览结果未完成";ShowError(error);UpdateBrowserEmptyState();
    }
    private void UpdateBrowserEmptyState()
    {
        string? error=browserScanError??browserEmptyError;
        // Observe the displayed controls, including the first page before a complete
        // snapshot exists. This presentation never binds or clears the result source.
        if(FilesGrid.Items.Count>0||FilesList.Items.Count>0||browserGroups is {Count:>0}&&groupedBrowserSource?.View is {} grouped&&ReferenceEquals(ActiveBrowser.ItemsSource,grouped))
        {BrowserEmptyState.Visibility=Visibility.Collapsed;return;}
        BrowserEmptyState.Visibility=Visibility.Visible;
        bool initial=rootId.Length==0&&!replacingRoot&&error is null;
        BrowserEmptyOpen.Visibility=initial?Visibility.Visible:Visibility.Collapsed;
        BrowserEmptyIcon.Glyph=error is not null?"\uE783":initial?"\uE8B7":"\uE721";
        if(error is not null)
        {
            BrowserEmptyTitle.Text="暂时无法显示浏览结果";
            BrowserEmptyDescription.Text=error;
            BrowserEmptyDetail.Text="请检查文件夹是否可访问，或调整筛选条件后重试。";
        }
        else if(initial)
        {
            BrowserEmptyTitle.Text="从一个文件夹开始";
            BrowserEmptyDescription.Text="穿透多层子文件夹，集中浏览图片和视频。";
            BrowserEmptyDetail.Text="选择顶部文件夹，也可以把文件夹拖到这里。";
        }
        else if(replacingRoot||queryBusy||scanTask is {IsCompleted:false}&&!scanStop.IsCancellationRequested)
        {
            BrowserEmptyTitle.Text=replacingRoot?"正在打开文件夹":scanTask is {IsCompleted:false}?"正在扫描子文件夹":"正在查找符合条件的文件";
            BrowserEmptyDescription.Text="发现符合条件的文件后会自动显示，无需手动刷新。";
            BrowserEmptyDetail.Text=Status.Text;
        }
        else
        {
            BrowserEmptyTitle.Text=scanStop.IsCancellationRequested?"扫描已停止":"当前视图没有可显示的文件";
            BrowserEmptyDescription.Text=scanStop.IsCancellationRequested?"已发现的文件仍保留。可使用顶部刷新按钮继续核对目录。":"可调整顶部的类型、搜索或筛选条件，或选择其他文件夹。";
            BrowserEmptyDetail.Text=ResultSummary.Text+"\n"+Status.Text;
        }
    }
}
