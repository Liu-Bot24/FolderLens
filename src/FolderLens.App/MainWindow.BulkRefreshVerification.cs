using System.Collections.Specialized;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyBulkRefresh(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;monitor?.Dispose();monitor=null;
        await RefreshQuery();await SelectBrowserOrdinal(results!,0,lifetime.Token);
        var anchor=selected??throw new InvalidOperationException("Missing fixture selection.");
        await WaitUntil(()=>anchor.Thumbnail is not null,TimeSpan.FromSeconds(10));
        var bitmap=anchor.Thumbnail;long original=resultHandle!.Count;var sequence=flatBrowserItems!;
        Shell.UpdateLayout();var viewport=CapturePublicationViewport(ActiveBrowser);
        int notifications=0;sequence.CollectionChanged+=Observe;
        void Observe(object? sender,NotifyCollectionChangedEventArgs e){notifications++;if(e.Action!=NotifyCollectionChangedAction.Reset)throw new InvalidOperationException("Bulk refresh emitted per-row notifications.");}
        try
        {
            await catalog!.Write(c=>
            {
                using var q=c.CreateCommand();q.CommandText="""
                    WITH RECURSIVE nums(n) AS(VALUES(1) UNION ALL SELECT n+1 FROM nums WHERE n<100000)
                    INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,mtime_utc_ticks,updated_revision,last_seen_scan_id)
                    SELECT 'audit-bulk-'||n,$root,$dir,'zz-audit-'||n||'.png','.png','zz-audit-'||n||'.png','zz-audit-'||n||'.png',CAST(printf('%012d',n) AS BLOB),X'FFFF',1,'synthetic','image','extension',1,1,0,
                        (SELECT last_seen_scan_id FROM Files WHERE entry_id=$entry) FROM nums;
                    UPDATE SchemaInfo SET catalog_revision=catalog_revision+1;
                    """;
                q.Parameters.AddWithValue("$root",rootId);q.Parameters.AddWithValue("$dir",anchor.Item!.DirectoryId);q.Parameters.AddWithValue("$entry",anchor.Item.EntryId);return q.ExecuteNonQuery();
            });
            await RefreshQuery(preserveViewport:true,scanPreview:true);Check(original+100000);
            await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="DELETE FROM Files WHERE entry_id GLOB 'audit-bulk-*'; UPDATE SchemaInfo SET catalog_revision=catalog_revision+1;";return q.ExecuteNonQuery();});
            await RefreshQuery(preserveViewport:true,scanPreview:true);Check(original);
            if(notifications!=2)throw new InvalidOperationException("Expected one reset per large publication.");
            report["bulkAddAndRemove100000"]=true;report["retainedDecodedThumbnailAndSelection"]=true;report["notifications"]=notifications;report["status"]="PASS";
        }
        finally{sequence.CollectionChanged-=Observe;}
        void Check(long count)
        {
            Shell.UpdateLayout();
            if(resultHandle!.Count!=count||results!.Count!=count||!ReferenceEquals(flatBrowserItems,sequence)||!ReferenceEquals(selected,anchor)||!ReferenceEquals(anchor.Thumbnail,bitmap))throw new InvalidOperationException("Bulk refresh lost count, selection, or decoded thumbnail.");
            if(results.CachedRows().Count()>4096)throw new InvalidOperationException("Bulk refresh materialized offscreen rows.");
            if(viewport is {} before&&ActiveBrowser.ContainerFromItem(before.Row) is FrameworkElement element&&Math.Abs(element.TransformToVisual(ActiveBrowser).TransformPoint(new(0,0)).Y-before.Top)>1)
                throw new InvalidOperationException("Bulk refresh moved the viewport anchor.");
        }
    }
}
