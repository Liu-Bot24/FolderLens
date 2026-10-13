using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed record BrowserSelectionCapture(ListViewBase View,object? Source,long Interaction,long Request,OrdinalRange[] Ranges,long? PreviewOrdinal);
    private BrowserSelectionCapture? CapturePublicationSelection(ListViewBase view)
    {
        if(pendingViewRestore is not null||results is null&&firstPageSequence.Length==0)return null;
        long? preview=selected is {} row&&(results?.Contains(row)==true||firstPageRows.Contains(row))?row.Ordinal:null;
        return new(view,view.ItemsSource,browserInteractionVersion,browserSelectionRequest,SelectedOrdinals(view).ToArray(),preview);
    }
    private bool OwnsPublicationSelection(BrowserSelectionCapture captured)=>captured.View==ActiveBrowser
        &&ReferenceEquals(captured.Source,captured.View.ItemsSource)&&captured.Interaction==browserInteractionVersion
        &&captured.Request==browserSelectionRequest&&captured.Ranges.SequenceEqual(SelectedOrdinals(captured.View));
    private SnapshotSelection MapFirstPageSelection(BrowserSelectionCapture captured,IReadOnlyList<SnapshotItem> matches)
    {
        var ids=firstPageSequence.Where(row=>captured.Ranges.Any(range=>row.Ordinal>=range.Start&&row.Ordinal<range.End))
            .Select(row=>row.Item!.EntryId).ToHashSet(StringComparer.Ordinal);
        string? preview=captured.PreviewOrdinal is {} ordinal&&ordinal<firstPageSequence.Length?firstPageSequence[(int)ordinal].Item?.EntryId:null;
        var ranges=OrdinalSelection.Normalize(matches.Where(item=>ids.Contains(item.EntryId)).Select(item=>new OrdinalRange(item.Ordinal,1)),matches.Count==0?0:matches.Max(item=>item.Ordinal)+1);
        return new(ranges,preview is null?null:matches.FirstOrDefault(item=>item.EntryId==preview)?.Ordinal);
    }
    private void RestorePublicationSelection(ListViewBase view,IReadOnlyList<OrdinalRange> ranges)
    {
        var projected=new List<OrdinalRange>();
        void Add(long start,long count)
        {
            if(count<=0)return;
            if(projected.Count>0&&projected[^1].End==start){var prior=projected[^1];projected[^1]=prior with{Count=prior.Count+count};}
            else projected.Add(new(start,count));
        }
        if(browserGroups is null)foreach(var range in ranges)Add(range.Start,range.Count);
        else
        {
            long offset=0;int first=0;
            foreach(var group in browserGroups)
            {
                if(group.IsCollapsed)continue;
                while(first<ranges.Count&&ranges[first].End<=group.Info.Start)first++;
                long end=group.Info.Start+group.Info.Count;
                for(int i=first;i<ranges.Count&&ranges[i].Start<end;i++)
                {long start=Math.Max(group.Info.Start,ranges[i].Start);Add(offset+start-group.Info.Start,Math.Min(end,ranges[i].End)-start);}
                offset+=group.Info.Count;
            }
        }
        bool syncing=syncingBrowserSelection;syncingBrowserSelection=true;
        try
        {
            // Group insertions can shift SelectedIndex while SelectedRanges
            // still contains old offsets. Reset through the native selection
            // owner before restoring ranges in the new group projection.
            view.SelectedIndex=-1;
            foreach(var range in projected)view.SelectRange(new ItemIndexRange(checked((int)range.Start),checked((uint)range.Count)));
        }
        finally{syncingBrowserSelection=syncing;}
    }
}
