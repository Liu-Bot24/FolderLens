using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<CancellationToken,Task>? verifySelectionMapBarrier;
    private async Task<SnapshotItem[]> FunctionalSelectedItems()
    {
        var handle=resultHandle!;var items=new List<SnapshotItem>();
        foreach(var range in SelectedOrdinals(ActiveBrowser))for(long position=range.Start;position<range.End;position+=256)
            items.AddRange(await catalog!.ReadPage(handle.Id,position,(int)Math.Min(256,range.End-position),lifetime.Token));
        return items.ToArray();
    }
    private async Task VerifyFunctionalSelectionBoundaries(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));source=Path.Combine(source,"multi-boundaries");
        for(int group=0;group<3;group++)
        {
            string directory=Path.Combine(source,$"G{group}");Directory.CreateDirectory(directory);
            for(int i=0;i<20;i++)await File.WriteAllBytesAsync(Path.Combine(directory,$"middle-{i:D2}.png"),sample);
        }
        var checks=new List<object>();report["checks"]=checks;
        foreach(bool details in new[]{false,true})foreach(bool grouped in new[]{false,true})
        {
            folderGrouping=new(grouped,"first","name","asc","all");sortDescending=false;SelectTag(SortField,"name");
            ChooseBrowserView(details?DetailsMode:ThumbnailMode,new RoutedEventArgs());await OpenRoot(source);await SettleFunctionalCollection();await RefreshQuery();
            ActiveBrowser.SelectedIndex=-1;foreach(int index in new[]{2,22,42})ActiveBrowser.SelectRange(new ItemIndexRange(index,1));
            await WaitUntil(()=>!previewLoading&&!browserMovementPending,TimeSpan.FromSeconds(5));
            string[] expected=(await FunctionalSelectedItems()).Select(item=>item.EntryId).Order().ToArray();
            async Task Check(string step)
            {
                var actual=(await FunctionalSelectedItems()).Select(item=>item.EntryId).Order().ToArray();bool same=expected.SequenceEqual(actual);
                int cached=results!.CachedRows().Count();checks.Add(new{details,grouped,step,same,selected=actual.Length,cached});
                if(!same||cached>4096)throw new InvalidOperationException("Native multi-selection or virtualization changed: "+step);
            }
            async Task Scan(){await new DirectoryIndexer(catalog!,ScanWorkerClient.FindExecutable(ScanWorkerDirectory)).Scan(rootId,source,epoch,true,[],null,lifetime.Token);await RefreshQuery(preserveViewport:true,scanPreview:true);}
            string suffix=$"{details}-{grouped}";
            await File.WriteAllBytesAsync(Path.Combine(source,"G0","zz-tail-"+suffix+".png"),sample);await Scan();await Check("tail-append");
            var anchor=(await FunctionalSelectedItems())[0];
            await catalog!.Write(c=>
            {
                using var q=c.CreateCommand();q.CommandText="""
                    WITH RECURSIVE nums(n) AS(VALUES(1) UNION ALL SELECT n+1 FROM nums WHERE n<10000)
                    INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,mtime_utc_ticks,updated_revision,last_seen_scan_id)
                    SELECT 'functional-prefix-'||n,$root,$dir,'aa-prefix-'||n||'.png','.png','G0/aa-prefix-'||n||'.png','functional-prefix-'||n,CAST(printf('%012d',n) AS BLOB),X'00',1,'synthetic','image','extension',1,1,0,
                        (SELECT last_seen_scan_id FROM Files WHERE entry_id=$entry) FROM nums;
                    UPDATE SchemaInfo SET catalog_revision=catalog_revision+1;
                    """;
                q.Parameters.AddWithValue("$root",rootId);q.Parameters.AddWithValue("$dir",anchor.DirectoryId);q.Parameters.AddWithValue("$entry",anchor.EntryId);return q.ExecuteNonQuery();
            });
            await RefreshQuery(preserveViewport:true,scanPreview:true);await Check("10000-prepend-reset");
            await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="DELETE FROM Files WHERE entry_id GLOB 'functional-prefix-*';UPDATE SchemaInfo SET catalog_revision=catalog_revision+1";return q.ExecuteNonQuery();});
            await RefreshQuery(preserveViewport:true,scanPreview:true);await Check("10000-remove-reset");
            var victim=(await FunctionalSelectedItems())[0];string path=Path.Combine(source,victim.RelativePath);File.Move(path,path+".held");
            expected=expected.Where(id=>id!=victim.EntryId).ToArray();await Scan();await Check("one-selected-file-removed");
            if(details)
            {
                string before=resultHandle!.Id;InvokeNativeButton(detailSortButtons[4]);await WaitUntil(()=>!queryBusy&&resultHandle?.Id!=before,TimeSpan.FromSeconds(5));await Check("detail-size-header");
            }
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            verifySelectionMapBarrier=async token=>{verifySelectionMapBarrier=null;entered.TrySetResult();await release.Task.WaitAsync(token);};
            string prior=resultHandle!.Id;InvokeNativeButton(Descending);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));ActiveBrowser.SelectedIndex=-1;ActiveBrowser.SelectRange(new ItemIndexRange(5,2));
                expected=(await FunctionalSelectedItems()).Select(item=>item.EntryId).Order().ToArray();release.TrySetResult();
                await WaitUntil(()=>!queryBusy&&resultHandle?.Id!=prior,TimeSpan.FromSeconds(8));await Check("new-multiselection-during-map-wins");
            }
            finally{release.TrySetResult();verifySelectionMapBarrier=null;}
        }
        var deleted=await FunctionalSelectedItems();if(deleted.Length!=2)throw new InvalidOperationException("Recycle fixture did not contain two selected files.");
        await OperateFile(FileOperationKind.Recycle);if(shellRefreshTask is not null)await shellRefreshTask;
        if(deleted.Any(item=>File.Exists(Path.Combine(source,item.RelativePath)))||fileOperationBusy)throw new InvalidOperationException("Native multiselection recycle failed.");
        report["nativeRecycleRemovedBothSelectedFiles"]=true;report["status"]="PASS";
    }
}
