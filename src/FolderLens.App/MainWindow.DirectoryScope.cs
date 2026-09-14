using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private void UpdateDirectoryScopeBanner(FilterSpec displayed)
    {
        bool scoped=displayed.DirectoryScope.Length>0||displayed.ScopeDirectFiles;
        DirectoryScopePanel.Visibility=scoped?Visibility.Visible:Visibility.Collapsed;
        DirectoryScopeLabel.Text=$"浏览范围：{(displayed.DirectoryScope.Length==0?"根目录":displayed.DirectoryScope)}{(displayed.ScopeDirectFiles?" · 仅当前文件夹中的文件":"")}";
        ToolTipService.SetToolTip(DirectoryScopeLabel,$"扫描根：{root}\n{DirectoryScopeLabel.Text}");
    }
    private async Task BrowseCapacityDirectory(string expectedRoot,string path,bool directFiles)
    {
        long requestedForeground=WindowFocus.Foreground;
        if(closing||replacingRoot||rootId!=expectedRoot)throw new InvalidOperationException("主窗口已切换文件夹，请关闭此统计窗口后重新打开。");
        await ReturnToBrowser();
        if(closing||replacingRoot||rootId!=expectedRoot)throw new OperationCanceledException("浏览目录已变化。");
        var current=CaptureView();
        var next=current with{Filter=current.Filter with{DirectoryScope=path,ScopeDirectFiles=directFiles},SelectedPath=null,ScrollAnchorPath=null,ScrollOffset=0,Preview=null};
        long rootVersion=rootChangeVersion;
        await RestoreSavedView(next);
        if(closing||rootId!=expectedRoot||rootVersion!=rootChangeVersion)throw new OperationCanceledException("浏览目录已变化。");
        if(resultHandle?.Generation!=generation)throw new InvalidOperationException("目录范围结果未建立，请在主窗口查看错误后重试。");
        WindowFocus.Show(this,requestedForeground);
    }
    private async void ClearDirectoryScope(object sender,RoutedEventArgs args)
    {
        try
        {
            var current=CaptureView();await RestoreSavedView(current with{Filter=current.Filter with{DirectoryScope="",ScopeDirectFiles=false}});
        }
        catch(Exception error){ShowError(error);}
    }
}
