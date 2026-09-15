using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private long directoryNavigationRequest;
    private string BrowsedDirectory=>activeCollectionId is null&&root.Length>0
        ?Path.Combine(root,CurrentFilter().DirectoryScope):root;
    private async Task<bool> TryBrowseCurrentRoot(string path,long navigation)
    {
        if(closing||replacingRoot||!browserRootReady||catalog is null||activeCollectionId is not null||rootId.Length==0||
            scannedPolicy is null||!scannedPolicy.HasSameScanPolicy(CurrentFilter()))return false;
        string? relative=DirectoryBrowseScope.Relative(root,path);
        if(relative is null)return false;
        string expectedRoot=rootId;long version=rootChangeVersion,view=viewRestoreRevision;
        bool known=await catalog.Read(c=>
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT 1 FROM Directories WHERE root_id=$root AND canonical_key=$path AND relative_path=$path AND entry_state='present'";
            cmd.Parameters.AddWithValue("$root",expectedRoot);cmd.Parameters.AddWithValue("$path",relative);
            return cmd.ExecuteScalar() is not null;
        },lifetime.Token);
        if(closing||version!=rootChangeVersion||navigation!=directoryNavigationRequest||view!=viewRestoreRevision)return true;
        if(!known)return false;
        await ReturnToBrowser();
        if(closing||version!=rootChangeVersion||navigation!=directoryNavigationRequest||view!=viewRestoreRevision)return true;
        var current=CaptureView();
        await RestoreSavedView(current with{Filter=current.Filter with{DirectoryScope=relative,ScopeDirectFiles=false},SelectedPath=null,ScrollAnchorPath=null,ScrollOffset=0,Preview=null},recheckDirectory:true);
        return true;
    }
    private void UpdateDirectoryScopeBanner(FilterSpec displayed)
    {
        if(activeCollectionId is null&&displayed.RootId==rootId&&RootPath.FocusState==FocusState.Unfocused)RootPath.Text=Path.Combine(root,displayed.DirectoryScope);
        bool scoped=displayed.DirectoryScope.Length>0||displayed.ScopeDirectFiles;
        DirectoryScopePanel.Visibility=scoped?Visibility.Visible:Visibility.Collapsed;
        DirectoryScopeLabel.Text=$"浏览范围：{(displayed.DirectoryScope.Length==0?"根目录":displayed.DirectoryScope)}{(displayed.ScopeDirectFiles?" · 仅当前文件夹中的文件":"")}";
        ToolTipService.SetToolTip(DirectoryScopeLabel,Path.Combine(root,displayed.DirectoryScope));
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
        await RestoreSavedView(next,recheckDirectory:true);
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
