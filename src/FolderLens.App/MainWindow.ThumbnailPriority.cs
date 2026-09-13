using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool IsThumbnailInViewport(ListViewBase view,FileRow row)
    {
        for(DependencyObject? node=view;node is not null;node=VisualTreeHelper.GetParent(node))
            if(node is UIElement{Visibility:Visibility.Collapsed})return false;
        if(view.ContainerFromItem(row) is not {} container)return false;
        // The realized container belongs to this control's current projection:
        // collapsed grid groups and the complete filmstrip have different indices.
        int index=view.IndexFromContainer(container);
        return index>=0&&view.ItemsPanelRoot switch
        {
            ItemsWrapGrid panel=>index>=panel.FirstVisibleIndex&&index<=panel.LastVisibleIndex,
            ItemsStackPanel panel=>index>=panel.FirstVisibleIndex&&index<=panel.LastVisibleIndex,
            _=>false
        };
    }
    private async Task VerifyThumbnailPriority(string source,Dictionary<string,object> report)
    {
        byte[] png=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int index=12;index<100;index++)await File.WriteAllBytesAsync(Path.Combine(source,"A",$"image-{index:D2}.png"),png);
        for(int index=0;index<100;index++)await File.WriteAllBytesAsync(Path.Combine(source,"B",$"image-{index:D2}.png"),png);
        folderGrouping=new(true,"all","name","asc","matches");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var first=browserGroups!.Single(group=>group.Info.RelativePath=="A");
        var second=browserGroups!.Single(group=>group.Info.RelativePath=="B");
        ToggleFolderGroup(first);Shell.UpdateLayout();
        var row=(FileRow)results![checked((int)second.Info.Start)]!;
        FilesGrid.ScrollIntoView(row,ScrollIntoViewAlignment.Leading);FilesGrid.UpdateLayout();
        await WaitUntil(()=>FilesGrid.ContainerFromItem(row) is not null,TimeSpan.FromSeconds(10));
        var buffered=(FileRow)results[checked((int)(second.Info.Start+second.Info.Count-1))]!;
        var gate=new PriorityAdmissionGate(1);
        await gate.WaitAsync(()=>false,lifetime.Token);
        var bufferRequest=gate.WaitAsync(()=>IsThumbnailInViewport(FilesGrid,buffered),lifetime.Token);
        var visibleRequest=gate.WaitAsync(()=>IsThumbnailInViewport(FilesGrid,row),lifetime.Token);
        report["snapshotOrdinal"]=row.Ordinal;
        report["projectedIndex"]=FilesGrid.IndexFromContainer(FilesGrid.ContainerFromItem(row));
        report["visiblePriority"]=IsThumbnailInViewport(FilesGrid,row);
        report["bufferPriority"]=IsThumbnailInViewport(FilesGrid,buffered);
        gate.Release();var admitted=await Task.WhenAny(bufferRequest,visibleRequest).WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();await Task.WhenAll(bufferRequest,visibleRequest);gate.Release();
        if(admitted!=visibleRequest||!(bool)report["visiblePriority"]||(bool)report["bufferPriority"])throw new InvalidOperationException("Collapsed-grid viewport request did not precede the buffered request.");
        BrowserPane.Visibility=Visibility.Collapsed;
        try{report["hiddenAncestorExcluded"]=!IsThumbnailInViewport(FilesGrid,row);if(!(bool)report["hiddenAncestorExcluded"])throw new InvalidOperationException("隐藏祖先下的列表仍抢占可见缩略图优先级。");}
        finally{BrowserPane.Visibility=Visibility.Visible;}
        report["status"]="PASS";
    }
}
