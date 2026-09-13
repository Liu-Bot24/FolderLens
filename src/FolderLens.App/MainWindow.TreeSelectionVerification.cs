using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyTreeCurrentFolder(string directory,byte[] png,Dictionary<string,object> report)
    {
        for(int i=0;i<60;i++)Directory.CreateDirectory(Path.Combine(directory,$"a-{i:D2}"));
        string target=Path.Combine(directory,"z-selected");Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target,"preview.png"),png);
        var stages=new List<object>();report["stages"]=stages;
        foreach(var (label,path) in new[]{("initial",target),("parent",directory),("return",target)})
        {
            await OpenRoot(path);
            if(physicalTreeTask is not null)await physicalTreeTask;
            if(treeRefreshTask is not null)await treeRefreshTask;
            Shell.UpdateLayout();await Task.Delay(120);
            var selected=FolderTree.SelectedNode;
            var item=activeTreeRoot is null?null:FolderTree.ContainerFromNode(activeTreeRoot) as TreeViewItem;
            var bounds=item?.TransformToVisual(FolderTree).TransformBounds(new(0,0,item.ActualWidth,item.ActualHeight));
            stages.Add(new{label,selected=ReferenceEquals(selected,activeTreeRoot),highlight=item?.IsSelected,top=bounds?.Top,bottom=bounds?.Bottom,height=FolderTree.ActualHeight});
            if(selected!=activeTreeRoot||item?.IsSelected!=true)throw new InvalidOperationException(label+": 当前目录没有保持实际选中高亮。");
            if(bounds is null||bounds.Value.Top<0||bounds.Value.Bottom>FolderTree.ActualHeight+1)throw new InvalidOperationException(label+": 当前目录位于视口之外。");
        }
        report["status"]="PASS";
    }
}
