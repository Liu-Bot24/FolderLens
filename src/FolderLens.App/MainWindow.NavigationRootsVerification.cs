using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyNavigationRoots(string source,Dictionary<string,object> report)
    {
        await OpenRoot(Path.Combine(source,"A"));if(physicalTreeTask is not null)await physicalTreeTask;
        var computer=FolderTree.RootNodes.SingleOrDefault(node=>node.Content?.ToString()=="此电脑")??throw new InvalidOperationException("目录树缺少固定的此电脑入口。");
        foreach(string label in new[]{"桌面","下载","文档","图片","用户文件夹"})
            if(!FolderTree.RootNodes.Any(node=>node.Content is FolderNode folder&&folder.Label==label))throw new InvalidOperationException("缺少常用目录入口："+label);
        foreach(string drive in Environment.GetLogicalDrives())
            if(!computer.Children.Any(node=>node.Content is FolderNode folder&&string.Equals(folder.Path,drive,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("此电脑缺少磁盘入口。");
        var roots=FolderTree.RootNodes.ToArray();
        var parent=activeTreeRoot?.Parent??throw new InvalidOperationException("当前目录没有父目录。");
        await ExpandFolderNode(parent);
        var sibling=parent.Children.Single(node=>node.Content is FolderNode folder&&folder.Path==Path.Combine(source,"B"));
        await NavigateFolder((FolderNode)sibling.Content);
        if(root!=Path.Combine(source,"B")||!FolderTree.RootNodes.SequenceEqual(roots))throw new InvalidOperationException("直接目录导航失败或重建了固定入口。");
        await NavigateFolder((FolderNode)parent.Content);
        if(physicalTreeTask is not null)await physicalTreeTask;
        await ExpandFolderNode(activeTreeRoot!);
        var child=activeTreeRoot!.Children.Single(node=>node.Content is FolderNode folder&&folder.Path==Path.Combine(source,"A"));
        await NavigateFolder((FolderNode)child.Content);
        if(root!=Path.Combine(source,"A")||!FolderTree.RootNodes.SequenceEqual(roots))throw new InvalidOperationException("逐层展开进入目录失败。");
        report["persistentPlacesAndDrives"]=true;report["directSiblingParentChildNavigation"]=true;report["status"]="PASS";
    }
}
