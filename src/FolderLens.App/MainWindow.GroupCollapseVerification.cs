using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyGroupCollapse(string directory,byte[] png,Dictionary<string,object> report)
    {
        static IEnumerable<DependencyObject> Children(DependencyObject node)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            {var child=VisualTreeHelper.GetChild(node,i);yield return child;foreach(var nested in Children(child))yield return nested;}
        }
        await File.WriteAllBytesAsync(Path.Combine(directory,"B","second.png"),png);
        folderGrouping=new(true,Field:"name",Direction:"asc");UpdateGroupingButton();
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var source=results!;var first=browserGroups![0];var second=browserGroups[1];var binding=ActiveBrowser.ItemsSource;
        var row=(FileRow)source[(int)second.Info.Start]!;await source.EnsureLoaded(row,lifetime.Token);
        ToggleFolderGroup(first);Shell.UpdateLayout();
        if(!ReferenceEquals(results,source))throw new InvalidOperationException("折叠操作替换了结果快照。");
        // Background metadata may legitimately publish another snapshot while the
        // test awaits. Check its visible behavior rather than the old source identity.
        await RefreshQuery(scanPreview:true);await Task.Delay(60);
        first=browserGroups!.Single(group=>group.Info.Id==first.Info.Id);second=browserGroups.Single(group=>group.Info.Id==second.Info.Id);
        source=results!;row=(FileRow)source[(int)second.Info.Start]!;await source.EnsureLoaded(row,lifetime.Token);
        if(!first.IsCollapsed||first.Items.Count!=0||ActiveBrowser.Items.Count!=second.Info.Count)throw new InvalidOperationException($"后台更新后折叠状态={first.IsCollapsed};组内={first.Items.Count};可见={ActiveBrowser.Items.Count};期望={second.Info.Count}。");
        await WaitUntil(()=>row.Thumbnail is not null,TimeSpan.FromSeconds(8));
        var retainedBinding=ActiveBrowser.ItemsSource;var retainedContainer=ActiveBrowser.ContainerFromItem(row);var retainedThumbnail=row.Thumbnail;
        string addedDirectory=Path.Combine(directory,"C");Directory.CreateDirectory(addedDirectory);string addedFile=Path.Combine(addedDirectory,"third.png");
        await File.WriteAllBytesAsync(addedFile,png);
        await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);await RefreshQuery(scanPreview:true);
        if(browserGroups.Count!=3)throw new InvalidOperationException("折叠期间新增的组未发布。");
        File.Delete(addedFile);
        await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);await RefreshQuery(scanPreview:true);Shell.UpdateLayout();
        if(browserGroups.Count!=2||!ReferenceEquals(first,browserGroups[0])||!ReferenceEquals(second,browserGroups[1])||!ReferenceEquals(retainedBinding,ActiveBrowser.ItemsSource)||!ReferenceEquals(retainedContainer,ActiveBrowser.ContainerFromItem(row))||!ReferenceEquals(retainedThumbnail,row.Thumbnail))
            throw new InvalidOperationException("存在折叠组时，增删其他组重建了未变化的可见图片。");
        report["collapsedNeighborInsertRemoveRetainsVisibleCard"]=true;source=results!;
        ActiveBrowser.SelectedIndex=0;
        var ranges=SelectedOrdinals(ActiveBrowser);
        if(ranges.Count!=1||ranges[0].Start!=second.Info.Start||!ReferenceEquals(SelectionPreview(ActiveBrowser),row))throw new InvalidOperationException("折叠后选择映射到错误文件。");
        DetailsMode.IsChecked=true;ToggleView(this,new());Shell.UpdateLayout();
        if(ActiveBrowser.Items.Count!=second.Info.Count||!ReferenceEquals(ActiveBrowser.SelectedItem,row))throw new InvalidOperationException("详情视图未保留折叠与选择。");
        ToggleFolderGroup(second);Shell.UpdateLayout();await Task.Delay(60);
        if(ActiveBrowser.Items.Count!=0||browserGroups.Count!=2||!Children(ActiveBrowser).OfType<TextBlock>().Any(text=>text.Text==second.Title))throw new InvalidOperationException("全部折叠后标题未保留。");
        UpdateBrowserEmptyState();if(BrowserEmptyState.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("全部折叠时空状态覆盖了分组标题。");
        ToggleFolderGroup(first);ToggleFolderGroup(second);Shell.UpdateLayout();
        if(ActiveBrowser.Items.Count!=source.Count)throw new InvalidOperationException("展开后数量不正确。");
        for(int i=0;i<source.Count;i++)if(((FileRow)ActiveBrowser.Items[i]).Ordinal!=i)throw new InvalidOperationException("展开后顺序改变。");
        ToggleFolderGroup(first);
        var priorView=ActiveBrowser.ItemsSource;
        UpdateBrowserResults(source,[first.Info],new Dictionary<string,IReadOnlyList<RangeEdit>>{{first.Info.Id,Array.Empty<RangeEdit>()}});Shell.UpdateLayout();
        if(!ReferenceEquals(priorView,ActiveBrowser.ItemsSource)||!ReferenceEquals(first,browserGroups[0]))throw new InvalidOperationException("移除分组替换了当前视图或保留分组。");
        if(browserGroups.Count!=1||!browserGroups[0].IsCollapsed||ActiveBrowser.Items.Count!=0)throw new InvalidOperationException("后台移除其他组时折叠状态丢失。");
        using var large=new VirtualResults(100000,(_,_)=>throw new InvalidOperationException("折叠不应读取文件。"));
        var info=new SnapshotGroup("large","large",0,100000,0,100000,"ready","all");
        var group=new BrowserFileGroup(large,info);group.ToggleCollapsed();
        group.Update(large,info with{Count=99999,MatchCount=99999},[new RangeEdit(99999,1,0)]);group.ToggleCollapsed();
        if(group.Items.Count!=99999||large.CachedRows().Any())throw new InvalidOperationException("隐藏组更新或展开提前实例化大量文件。");
        AttachBrowserView(null);if(groupedBrowserSource is not null)groupedBrowserSource.Source=null;
        BindBrowserResults(large,[info]);var largeGroup=browserGroups![0];
        ToggleFolderGroup(largeGroup);ToggleFolderGroup(largeGroup);Shell.UpdateLayout();
        int created=large.CachedRows().Count();if(created>1024)throw new InvalidOperationException($"展开提前实例化 {created} 行。");
        AttachBrowserView(null);groupedBrowserSource!.Source=null;
        report["largeGroupVisibleRowsMaterialized"]=created;
        report["selectionAndViewSwitch"]=true;report["headersRetained"]=true;report["largeGroupRowsMaterialized"]=0;report["status"]="PASS";
    }
}
