using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private void VerifyInitialNavigation()
    {
        if(catalog is not null||root.Length!=0)throw new InvalidOperationException("启动导航检查必须在索引初始化和打开目录之前执行。");
        if(!FolderTree.RootNodes.Any(node=>node.Content?.ToString()=="此电脑")||
           !FolderTree.RootNodes.Any(node=>node.Content?.ToString()=="收藏夹"))throw new InvalidOperationException("启动默认状态缺少基础导航入口；入口不能等待索引库初始化。");
        foreach(string label in new[]{"桌面","下载","文档","图片","用户文件夹"})
            if(!FolderTree.RootNodes.Any(node=>node.Content is FolderNode folder&&folder.Label==label))throw new InvalidOperationException("启动默认状态缺少常用目录："+label);
    }
    private async Task VerifyNavigationRoots(string source,Dictionary<string,object> report)
    {
        report["navigationAvailableBeforeCatalogAndRoot"]=true;
        FolderTree.UpdateLayout();await Task.Delay(100);
        IEnumerable<Microsoft.UI.Xaml.DependencyObject> Descendants(Microsoft.UI.Xaml.DependencyObject parent)
        {
            for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);i++){var child=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
        }
        var icons=Descendants(FolderTree).OfType<FontIcon>().Where(icon=>icon.FontSize==16&&!string.IsNullOrEmpty(icon.Glyph)).ToArray();
        if(icons.Length<7)throw new InvalidOperationException("目录树没有渲染常用位置的原生图标。");
        var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();await bitmap.RenderAsync(TreePane);
        using(var file=File.Create(Path.Combine(dataDirectory,"navigation-default.png")))
        using(var stream=System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file))
        {
            var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
            var buffer=await bitmap.GetPixelsAsync();byte[] pixels=new byte[buffer.Length];Windows.Storage.Streams.DataReader.FromBuffer(buffer).ReadBytes(pixels);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels);await encoder.FlushAsync();
        }
        report["renderedNavigationIcons"]=icons.Length;
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
