using System.Text.Json;
using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string? firstPageFilter;
    private FileRow[] firstPageSequence=[];
    private bool automaticQueryPending;
    private Task queryCompletion=Task.CompletedTask;
    private bool BrowserSequenceLocked=>resultHandle is not null&&(selected is not null||immersive||fullScreen);
    private bool FirstPageMatches(FilterSpec filter)=>firstPageSequence.Length>0&&firstPageFilter==JsonSerializer.Serialize(filter);
    private void PublishFirstPage(FilterSpec filter,IReadOnlyList<SnapshotItem> items)
    {
        CancelThumbnails();
        firstPageSequence=items.Select(item=>{var row=new FileRow(item.Ordinal);row.SetPresentation(GridCardWidth,gridShowPaths);row.SetCollectionView(filter.CollectionId is not null);row.Fill(item);return row;}).ToArray();
        firstPageFilter=JsonSerializer.Serialize(filter);firstPageRows=new(firstPageSequence);
        groupedBrowserSource?.Dispose();groupedBrowserSource=null;browserGroups=null;
        var sequence=firstPageSequence;
        flatBrowserItems=new(sequence.Length,i=>sequence[i],item=>item is FileRow row?Array.IndexOf(sequence,row):-1);
        AttachBrowserView(flatBrowserItems);
        if(viewerStrip is not null)viewerStrip.ItemsSource=flatBrowserItems;
    }
    private IReadOnlyList<RangeEdit> PromoteFirstPage(VirtualResults next,IReadOnlyList<SnapshotItem> matches)
    {
        var byId=matches.ToDictionary(item=>item.EntryId);
        var anchors=new List<(int Old,int New)>();
        for(int i=0;i<firstPageSequence.Length;i++)
        {
            var row=firstPageSequence[i];var previous=row.Item!;
            if(!byId.TryGetValue(previous.EntryId,out var item)||previous.Version!=item.Version||previous.RelativePath!=item.RelativePath||previous.Kind!=item.Kind||previous.Bytes!=item.Bytes||previous.Allocated!=item.Allocated)continue;
            next.Retain(row,checked((int)item.Ordinal),item.Group);row.Fill(item);anchors.Add((i,checked((int)item.Ordinal)));
        }
        return new SnapshotSplice(0,firstPageSequence.Length,next.Count).Preserve(anchors);
    }
    private void UpdatePromotedFirstPage(VirtualResults next,IReadOnlyList<RangeEdit> changes)
    {
        // A complete snapshot may add millions of positions. Reset the virtual
        // index once for a large publication; retained rows keep their decoded
        // thumbnails. Do not generate a managed row for each inserted position.
        if(changes.Sum(change=>(long)change.Added+change.Removed)>4096)
            flatBrowserItems!.Replace(next.Count,i=>(FileRow)next[i]!,next.IndexOf);
        else flatBrowserItems!.UpdateRanges(changes,i=>(FileRow)next[i]!,next.IndexOf);
        firstPageRows.Clear();firstPageSequence=[];firstPageFilter=null;
    }
}
