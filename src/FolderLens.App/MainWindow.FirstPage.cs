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
    private bool BrowserSequenceLocked=>ScanPreviewRefresh.SequenceLocked(resultHandle is not null,selected is not null,immersive,fullScreen,slideShow);
    private bool FirstPageMatches(FilterSpec filter)=>firstPageSequence.Length>0&&firstPageFilter==JsonSerializer.Serialize(filter);
    private string? RetireMismatchedResults(FilterSpec filter)
    {
        bool matches=resultHandle is not null
            ?lastAppliedFilter is not null&&QueryFilterHash(lastAppliedFilter with{Sort=filter.Sort,Grouping=filter.Grouping})==QueryFilterHash(filter)
            :firstPageSequence.Length==0||FirstPageMatches(filter);
        if(matches)return null;
        // Retaining a failed refresh is useful only for the same filter. Rows
        // from another category must not remain actionable under the new label.
        return RetireBrowserResults();
    }
    private string? RetireBrowserResults()
    {
        ClearResultSelection();CancelThumbnails();AttachBrowserView(null);
        if(viewerStrip is not null)viewerStrip.ItemsSource=null;
        groupedBrowserSource?.Dispose();groupedBrowserSource=null;browserGroups=null;flatBrowserItems=null;
        results?.Dispose();results=null;
        firstPageSequence=[];firstPageFilter=null;firstPageRows.Clear();
        string? previous=resultHandle?.Id;resultHandle=null;
        return previous;
    }
    private async Task RejectBrowserFilter(Exception error)
    {
        // Invalid submitted controls are also a new intent. They must retire a
        // still-running old query rather than let it repopulate the new category.
        CancelPendingSearch();generation++;queryRequest++;queryStop.Cancel();metadataDemandStop.Cancel();
        automaticQueryPending=false;metadataRefreshPending=false;explicitMetadataPending=false;queryBusy=false;
        string? previous=RetireBrowserResults();submittedSearch=Search.Text;
        ActiveFilterSummary.Visibility=Microsoft.UI.Xaml.Visibility.Collapsed;
        ShowBrowserError(error);
        var attempt=new QueryAttempt(queryRequest,generation,epoch,rootId,false);
        if(previous is not null&&catalog is not null)
        {
            try{await catalog.ReleaseSnapshot(previous);}
            catch(Exception releaseError)
            {
                await RecordQueryFailure(releaseError,"retireInvalidFilter",attempt,previous);
                if(attempt.Request==queryRequest&&attempt.Generation==generation&&!closing)ShowError(releaseError);
            }
        }
    }
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
        flatBrowserItems!.UpdateRanges(changes,i=>(FileRow)next[i]!,next.IndexOf);
        firstPageRows.Clear();firstPageSequence=[];firstPageFilter=null;
    }
}
