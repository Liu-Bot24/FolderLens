using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string? browserEmptyError;
    private string? browserScanError;
    private void ShowScanError(Exception error)
    {
        browserScanError=UserMessages.Error(error);reconcilePending=false;ShowBrowserError(error);
    }
    private void ClearScanError()
    {
        if(browserEmptyError==browserScanError)browserEmptyError=null;
        browserScanError=null;UpdateBrowserEmptyState();
    }
    private void InitializeBrowserEmptyState()
    {
        ResultSummary.RegisterPropertyChangedCallback(TextBlock.TextProperty,(_,_)=>UpdateBrowserEmptyState());
        Status.RegisterPropertyChangedCallback(TextBlock.TextProperty,(_,_)=>UpdateBrowserEmptyState());
        UpdateBrowserEmptyState();
    }
    private void ShowBrowserError(Exception error)
    {
        browserEmptyError=UserMessages.Error(error);ResultSummary.Text="文件列表未加载完成";ShowError(error);UpdateBrowserEmptyState();
    }
    private void UpdateBrowserEmptyState()
    {
        UpdateCommandAvailability();
        BrowserScanErrorBar.Message=browserScanError??"";
        BrowserScanErrorBar.IsOpen=browserScanError is not null;
        string? error=browserScanError??browserEmptyError;
        bool scanning=scanTask is {IsCompleted:false}&&!scanStop.IsCancellationRequested;
        bool loading=error is null&&(replacingRoot||queryBusy||scanning);
        BrowserLoadingState.Visibility=loading?Visibility.Visible:Visibility.Collapsed;
        BrowserLoadingRing.IsActive=loading;
        long displayed=resultHandle?.Count??firstPageSequence.Length;
        BrowserLoadingText.Text=scanning
            ?displayed==0?"正在加载首批文件…":"正在继续扫描文件夹，已显示 "+displayed.ToString("N0")+" 个文件…"
            :"正在整理文件列表…";
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
            BrowserEmptyDescription.Text="集中浏览文件夹及各层子文件夹中的文件。";
            BrowserEmptyDetail.Text="选择顶部文件夹，也可以把文件夹拖到这里。";
        }
        else if(replacingRoot||queryBusy||scanTask is {IsCompleted:false}&&!scanStop.IsCancellationRequested)
        {
            BrowserEmptyTitle.Text=activeBackgroundScan is {} currentScan&&scanScheduler.IsWaiting(currentScan.RootId)?"正在安排此文件夹的扫描":replacingRoot?"正在打开文件夹":scanTask is {IsCompleted:false}?"正在扫描子文件夹":"正在查找符合条件的文件";
            BrowserEmptyDescription.Text="首批文件准备好后会自动显示，随后继续加载。";
            BrowserEmptyDetail.Text=ScanStatusDescription();
        }
        else
        {
            BrowserEmptyTitle.Text=scanStop.IsCancellationRequested?"扫描已停止":"当前视图没有可显示的文件";
            BrowserEmptyDescription.Text=scanStop.IsCancellationRequested?"已发现的文件仍保留。可使用顶部刷新按钮继续核对目录。":"可调整顶部的类型、搜索或筛选条件，或选择其他文件夹。";
            BrowserEmptyDetail.Text=ResultSummary.Text+"\n"+Status.Text;
        }
    }
}
