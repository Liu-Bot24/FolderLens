using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string[] lastCollectionTargets=[];
    private bool quickCollectionUsed,quickCollectionBusy;
    private long collectionChangeVersion;
    private Func<ContentDialog,Func<bool,Task<bool>>,Task>? verifyCollectionDialog;
    private Func<Task>? verifyCollectionBadgeRead;
    private Task collectionBadgeTask=Task.CompletedTask;
    private bool collectionBadgesPending;
    private void RefreshCollectionBadges()
    {
        long version=++collectionChangeVersion;
        collectionBadgesPending=true;
        if(collectionBadgeTask.IsCompleted)collectionBadgeTask=RefreshCollectionBadgesCore(version);
    }
    private async Task RefreshCollectionBadgesCore(long version)
    {
        using var operation=browserWork.Enter();if(operation is null||catalog is null)return;
        try
        {
            do
            {
                collectionBadgesPending=false;version=collectionChangeVersion;
                var rows=visible.Where(row=>row.Item is not null).Select(row=>(Row:row,Item:row.Item!)).ToArray();
                foreach(var batch in rows.Chunk(256))
                {
                    var flags=await catalog.ReadCollectionFlags(batch.Select(value=>value.Item).ToArray(),lifetime.Token);
                    if(verifyCollectionBadgeRead is {} barrier)await barrier();
                    if(closing)return;
                    if(version!=collectionChangeVersion){collectionBadgesPending=true;break;}
                    for(int i=0;i<batch.Length;i++)
                    {
                        var captured=batch[i];if(!visible.Contains(captured.Row))continue;
                        if(captured.Row.Item is {} current&&SameCollectionObservation(current,captured.Item))captured.Row.SetCollected(flags[i]);
                        else collectionBadgesPending=true;
                    }
                }
                // Coalesce concurrent changes and invalidated observations into
                // one next pass; never create a query task for each retained row.
                if(collectionBadgesPending)await Task.Delay(25,lifetime.Token);
            }while(collectionBadgesPending&&!closing);
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(!closing)ShowError(error);}
    }
    private static bool SameCollectionObservation(SnapshotItem a,SnapshotItem b)=>
        a.EntryId==b.EntryId&&a.Version==b.Version&&a.PathRevision==b.PathRevision&&a.RelativePath==b.RelativePath&&
        a.DirectoryId==b.DirectoryId&&a.DirectoryLocationId==b.DirectoryLocationId&&a.BindingRevision==b.BindingRevision&&
        a.SourceRootId==b.SourceRootId&&a.SourceRootPath==b.SourceRootPath&&a.SourceRootEpoch==b.SourceRootEpoch;
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
