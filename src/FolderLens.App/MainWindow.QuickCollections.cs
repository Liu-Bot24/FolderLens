using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string[] lastCollectionTargets=[];
    private bool quickCollectionUsed,quickCollectionBusy;
    private long collectionChangeVersion;
    private Func<ContentDialog,Func<bool,Task<bool>>,Task>? verifyCollectionDialog;
    private void RefreshCollectionBadges()
    {
        collectionChangeVersion++;
        foreach(var row in visible.ToArray())_=LoadRowProperties(row,refresh:true);
    }
    private void QuickCollectDoubleTapped(object sender,DoubleTappedRoutedEventArgs args)=>args.Handled=true;
    private async void QuickCollectClicked(object sender,RoutedEventArgs args)
    {
        if((sender as FrameworkElement)?.DataContext is not FileRow row)return;
        try{await QuickCollect(row);}catch(OperationCanceledException){}catch(Exception error){ShowError(error);}
    }
    private async Task QuickCollect(FileRow row)
    {
        using var work=browserWork.Enter();if(work is null||closing||catalog is null||row.Item is null||quickCollectionBusy)return;
        var item=row.Item;quickCollectionBusy=true;row.SetQuickCollectBusy(true);
        try
        {
            await RefreshCollectionsTree();
            if(!quickCollectionUsed||lastCollectionTargets.Length==0||lastCollectionTargets.Any(id=>!fileCollections.Any(c=>c.Id==id)))
            {await CollectFiles(row);return;}
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);stop.CancelAfter(TimeSpan.FromSeconds(30));
            var targets=lastCollectionTargets.ToArray();
            int added=await catalog.ChangeCollectionItems(targets,[item],true,stop.Token);
            RefreshCollectionBadges();row.SetCollected(true);await RefreshCollectionsTree();
            Status.Text=(added==0?"已在收藏夹中：":"已收藏到：")+string.Join("、",targets.Select(CollectionLabel));
            if(activeCollectionId is not null||includedCollectionIds.Length+excludedCollectionIds.Length>0)await RefreshQuery(preserveViewport:true);
        }
        finally{row.SetQuickCollectBusy(false);quickCollectionBusy=false;}
    }
}
