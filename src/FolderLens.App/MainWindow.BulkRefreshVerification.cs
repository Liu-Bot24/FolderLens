using System.Collections.Specialized;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyBulkRefresh(string source,Dictionary<string,object> report)
    {
        folderGrouping=new();sortDescending=false;SelectTag(SortField,"name");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;monitor?.Dispose();monitor=null;
        await RefreshQuery();
        var anchor=(FileRow)results![0]!;
        // Native list selection is the user's single click, before opening the viewer.
        ActiveBrowser.SelectedItem=anchor;
        await WaitUntil(()=>ReferenceEquals(selected,anchor),TimeSpan.FromSeconds(5));
        if(immersive||fullScreen)throw new InvalidOperationException("Fixture opened the viewer instead of selecting a browser item.");
        await WaitUntil(()=>anchor.Thumbnail is not null,TimeSpan.FromSeconds(10));
        await WaitUntil(()=>!browserMovementPending&&!queryBusy,TimeSpan.FromSeconds(5));
        long original=resultHandle!.Count;var sequence=flatBrowserItems!;
        Shell.UpdateLayout();var viewport=CapturePublicationViewport(ActiveBrowser);
        double? AnchorTop()=>ActiveBrowser.ContainerFromItem(anchor) is Microsoft.UI.Xaml.FrameworkElement element
            ?element.TransformToVisual(ActiveBrowser).TransformPoint(new(0,0)).Y:null;
        double originalTop=AnchorTop()??throw new InvalidOperationException("Selected fixture row is not visible.");
        int renderedFrames=0,missingAnchorBitmapFrames=0,displacedFrames=0,missingSelectionFrames=0;
        void ObserveFrame(object? sender,object args)
        {
            renderedFrames++;
            if(anchor.Thumbnail is null)missingAnchorBitmapFrames++;
            if(!IsThumbnailInViewport(ActiveBrowser,anchor)||AnchorTop() is not {} top||Math.Abs(top-originalTop)>1)displacedFrames++;
            if(!ReferenceEquals(ActiveBrowser.SelectedItem,anchor))missingSelectionFrames++;
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering+=ObserveFrame;
        int notifications=0;sequence.CollectionChanged+=Observe;
        void Observe(object? sender,NotifyCollectionChangedEventArgs e){notifications++;if(e.Action!=NotifyCollectionChangedAction.Reset)throw new InvalidOperationException("Bulk refresh emitted per-row notifications.");}
        try
        {
            await catalog!.Write(c=>
            {
                using var q=c.CreateCommand();q.CommandText="""
                    WITH RECURSIVE nums(n) AS(VALUES(1) UNION ALL SELECT n+1 FROM nums WHERE n<100000)
                    INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,mtime_utc_ticks,updated_revision,last_seen_scan_id)
                    SELECT 'audit-bulk-'||n,$root,$dir,'zz-audit-'||n||'.png','.png','zz-audit-'||n||'.png','zz-audit-'||n||'.png',CAST(printf('%012d',n) AS BLOB),X'00',1,'synthetic','image','extension',1,1,0,
                        (SELECT last_seen_scan_id FROM Files WHERE entry_id=$entry) FROM nums;
                    UPDATE SchemaInfo SET catalog_revision=catalog_revision+1;
                    """;
                q.Parameters.AddWithValue("$root",rootId);q.Parameters.AddWithValue("$dir",anchor.Item!.DirectoryId);q.Parameters.AddWithValue("$entry",anchor.Item.EntryId);return q.ExecuteNonQuery();
            });
            await RefreshQuery(preserveViewport:true,scanPreview:true);await Check(original+100000);
            await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="DELETE FROM Files WHERE entry_id GLOB 'audit-bulk-*'; UPDATE SchemaInfo SET catalog_revision=catalog_revision+1;";return q.ExecuteNonQuery();});
            await RefreshQuery(preserveViewport:true,scanPreview:true);await Check(original);
            if(notifications!=2)throw new InvalidOperationException("Expected one reset per large publication.");
            if(renderedFrames==0||missingAnchorBitmapFrames!=0)throw new InvalidOperationException($"Selected thumbnail disappeared during publication: {missingAnchorBitmapFrames}/{renderedFrames} frames.");
            if(displacedFrames!=0||missingSelectionFrames!=0)throw new InvalidOperationException($"Selected image moved or lost its highlight during publication: position={displacedFrames}, selection={missingSelectionFrames}, frames={renderedFrames}.");
            report["bulkPrependAndRemove100000"]=true;report["retainedSelectionAndVisiblePosition"]=true;report["notifications"]=notifications;report["status"]="PASS";
        }
        finally
        {
            sequence.CollectionChanged-=Observe;Microsoft.UI.Xaml.Media.CompositionTarget.Rendering-=ObserveFrame;
            report["renderedFrames"]=renderedFrames;report["missingAnchorBitmapFrames"]=missingAnchorBitmapFrames;
            report["displacedFrames"]=displacedFrames;report["missingSelectionFrames"]=missingSelectionFrames;
        }
        async Task Check(long count)
        {
            Shell.UpdateLayout();
            if(resultHandle!.Count!=count||results!.Count!=count||!ReferenceEquals(flatBrowserItems,sequence)||!ReferenceEquals(selected,anchor))throw new InvalidOperationException("Bulk refresh lost count or selection.");
            if(results.CachedRows().Count()>4096)throw new InvalidOperationException("Bulk refresh materialized offscreen rows.");
            ApplyBrowserOffset();
            try{await WaitUntil(()=>AnchorTop() is {} top&&Math.Abs(top-originalTop)<=1&&IsThumbnailInViewport(ActiveBrowser,anchor),TimeSpan.FromSeconds(3));}
            finally{report["selectedVisibilityCheckpoint"]=new{count,originalTop,currentTop=AnchorTop(),visible=IsThumbnailInViewport(ActiveBrowser,anchor),originalOffset=viewport,currentOffset=FindScrollViewer(ActiveBrowser)?.VerticalOffset,ordinal=anchor.Ordinal};}
        }
    }
}
