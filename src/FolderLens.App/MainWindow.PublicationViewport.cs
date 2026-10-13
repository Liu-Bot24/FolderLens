using Microsoft.UI.Dispatching;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey=Windows.System.VirtualKey;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private HashSet<FileRow>? publicationRows;
    private bool keepBrowserStart=true;
    private bool browserContentProtected;
    private long browserInteractionVersion;
    private bool restoringBrowserSelection;
    private sealed record BrowserContentAnchor(FileRow Row,double Top);
    private sealed record BrowserOffsetOwner(ListViewBase View,object Source,long RootVersion,long InteractionVersion,double Offset,BrowserContentAnchor? Anchor=null,bool RevealRequested=false);
    private BrowserOffsetOwner? browserOffsetOwner;
    private bool browserOffsetQueued,applyingBrowserOffset,browserMovementPending;
    private uint? browserScrollPointer;
    private DispatcherQueueTimer? browserMovementTimer;
    private long browserMovementChanged;
    private bool viewportPublicationPending;

    private void RestoreBrowserSelection(ListViewBase view,FileRow row)
    {
        bool previous=restoringBrowserSelection;restoringBrowserSelection=true;
        try{view.SelectedItem=row;}finally{restoringBrowserSelection=previous;}
    }
    private void ResetBrowserStart()
    {
        keepBrowserStart=true;browserContentProtected=false;SetBrowserOffsetOwner(null);browserMovementPending=false;browserScrollPointer=null;browserMovementTimer?.Stop();viewportPublicationPending=false;
    }
    private void BrowserPointerIntent(object sender,PointerRoutedEventArgs args)
    {
        bool moving=args.Pointer.PointerDeviceType!=Microsoft.UI.Input.PointerDeviceType.Mouse;
        for(var node=args.OriginalSource as DependencyObject;node is not null&&!ReferenceEquals(node,sender);node=VisualTreeHelper.GetParent(node))
            if(node is ScrollBar){moving=true;break;}
        if(moving)browserScrollPointer=args.Pointer.PointerId;
        AcceptBrowserInteraction(moving);
    }
    private void BrowserWheelIntent(object sender,PointerRoutedEventArgs args)=>AcceptBrowserInteraction(changesViewport:true);
    private void BrowserPointerReleased(object sender,PointerRoutedEventArgs args)
    {
        if(browserScrollPointer!=args.Pointer.PointerId)return;
        browserScrollPointer=null;ContinueBrowserMovement();
    }
    private void BrowserKeyIntent(object sender,KeyRoutedEventArgs args)
    {
        if(args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Tab)
            AcceptBrowserInteraction(changesViewport:true);
    }
    private void AcceptBrowserInteraction(bool changesViewport=false)
    {
        if(closing||updatingBrowser)return;
        CancelPendingFileOpen();
        browserInteractionVersion++;pendingViewRestore=null;pendingPreviewRestore=null;
        browserContentProtected=true;
        if(changesViewport)BeginBrowserMovement();
        else SetBrowserOffsetOwner(null);
    }
    private void BeginBrowserMovement(bool protectContent=true)
    {
        SetBrowserOffsetOwner(null);browserContentProtected=protectContent;keepBrowserStart=false;browserMovementPending=true;ContinueBrowserMovement();
    }
    private void ContinueBrowserMovement()
    {
        if(closing||!browserMovementPending)return;
        if(browserMovementTimer is null)
        {
            browserMovementTimer=DispatcherQueue.CreateTimer();browserMovementTimer.Interval=TimeSpan.FromMilliseconds(180);browserMovementTimer.IsRepeating=false;
            browserMovementTimer.Tick+=(_,_)=>
            {
                if(closing||!browserMovementPending)return;
                if(browserScrollPointer is not null||Stopwatch.GetElapsedTime(browserMovementChanged)<TimeSpan.FromMilliseconds(180)){browserMovementTimer.Start();return;}
                browserMovementPending=false;
                if(!immersive)keepBrowserStart=(FindScrollViewer(ActiveBrowser)?.VerticalOffset??0)<=1;
                PublishPendingViewport();
            };
        }
        browserMovementChanged=Stopwatch.GetTimestamp();browserMovementTimer.Stop();browserMovementTimer.Start();
    }
    private void PublishPendingViewport()
    {
        if(closing||!viewportPublicationPending)return;
        viewportPublicationPending=false;_=RefreshQuery(preserveViewport:true,scanPreview:true);
    }
    private bool OwnsBrowserOffset(BrowserOffsetOwner owner)=>!closing&&!immersive&&owner.View==ActiveBrowser
        &&owner.RootVersion==rootChangeVersion&&owner.InteractionVersion==browserInteractionVersion
        &&ReferenceEquals(owner.Source,owner.View.ItemsSource);
    private BrowserContentAnchor? CapturePublicationAnchor(ListViewBase view)
    {
        if(!browserContentProtected||closing||immersive)return null;
        // A second batch can arrive before the previous native layout settles.
        // Carry its intended file position, never capture a transient displacement.
        if(browserOffsetOwner is {Anchor:{} held} owner&&OwnsBrowserOffset(owner))return held;
        BrowserContentAnchor? Capture(FileRow row)
        {
            if(!(firstPageRows.Contains(row)||results?.Contains(row)==true)||view.ContainerFromItem(row) is not FrameworkElement element||element.ActualHeight<=0)return null;
            double top=element.TransformToVisual(view).TransformPoint(new(0,0)).Y;
            return top+element.ActualHeight>0&&top<view.ActualHeight?new(row,top):null;
        }
        // Only follow a selection the user is still looking at. Scrolling away
        // from a selected file must not drag the viewport back to that file.
        if(selected is {} chosen&&Capture(chosen) is {} selectionAnchor)return selectionAnchor;
        BrowserContentAnchor? first=null;
        foreach(var pair in visibleContainers)
        {
            if(pair.Key.View!=view||Capture(pair.Value) is not {} candidate)continue;
            if(first is null||candidate.Top<first.Top||candidate.Top==first.Top&&candidate.Row.Ordinal<first.Row.Ordinal)first=candidate;
        }
        return first;
    }
    private static BrowserContentAnchor? RebindPublicationAnchor(BrowserContentAnchor? anchor,VirtualResults next,IReadOnlyList<FolderLens.Infrastructure.SnapshotItem> matches)
    {
        if(anchor is null)return null;
        if(next.Contains(anchor.Row))return anchor;
        // A changed version needs a fresh row/bitmap, but is still the same
        // visible file. Do not resurrect its old content merely to keep position.
        if(anchor.Row.Item is {} old&&matches.FirstOrDefault(item=>item.EntryId==old.EntryId) is {} item)
            return anchor with{Row=(FileRow)next[checked((int)item.Ordinal)]!};
        return null;
    }
    private void SetBrowserOffsetOwner(BrowserOffsetOwner? next)
    {
        var previous=browserOffsetOwner;browserOffsetOwner=next;
        if(previous?.Anchor is {} anchor&&!ReferenceEquals(anchor.Row,next?.Anchor?.Row)&&!visible.Contains(anchor.Row))
            ReleaseUnseenPublicationRow(anchor.Row);
    }
    private double CapturePublicationViewport(ListViewBase view)
    {
        if(keepBrowserStart)return 0;
        if(browserOffsetOwner is {} owner&&owner.View==view&&OwnsBrowserOffset(owner))
            return Math.Min(owner.Offset,Math.Max(0,FindScrollViewer(view)?.ScrollableHeight??owner.Offset));
        return Math.Max(0,FindScrollViewer(view)?.VerticalOffset??0);
    }
    private void RestoreBrowserStart(ListViewBase view)
    {
        if(keepBrowserStart)RestorePublicationViewport(view,0);
    }
    private void RestorePublicationViewport(ListViewBase view,double offset,BrowserContentAnchor? anchor=null)
    {
        if(closing||immersive||browserMovementPending||view.ItemsSource is not {} source)return;
        offset=double.IsFinite(offset)?Math.Max(0,offset):0;
        keepBrowserStart=anchor is null&&offset<=1;
        SetBrowserOffsetOwner(new(view,source,rootChangeVersion,browserInteractionVersion,offset,anchor));
        // Untouched folders stay at the start. Once browsing begins, preserve
        // the visible file through later native layout passes, not its old ordinal.
        view.UpdateLayout();ApplyBrowserOffset();QueueBrowserOffset();
    }
    private void QueueBrowserOffset()
    {
        if(closing||applyingBrowserOffset||browserOffsetQueued||browserOffsetOwner is null)return;
        browserOffsetQueued=true;
        if(!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,()=>
        {
            browserOffsetQueued=false;ApplyBrowserOffset();
        }))browserOffsetQueued=false;
    }
    private void ApplyBrowserOffset()
    {
        if(updatingBrowser||applyingBrowserOffset||browserMovementPending||browserOffsetOwner is not {} owner)return;
        if(!OwnsBrowserOffset(owner)){SetBrowserOffsetOwner(null);return;}
        if(FindScrollViewer(owner.View) is not {} scroll)return;
        applyingBrowserOffset=true;
        try
        {
            double target=owner.Offset;
            if(owner.Anchor is {} anchor)
            {
                if(owner.View.ContainerFromItem(anchor.Row) is not FrameworkElement)
                {
                    if(!owner.RevealRequested)
                    {
                        browserOffsetOwner=owner=owner with{RevealRequested=true};
                        owner.View.ScrollIntoView(anchor.Row,ScrollIntoViewAlignment.Leading);
                        owner.View.UpdateLayout();
                    }
                    if(owner.View.ContainerFromItem(anchor.Row) is not FrameworkElement)return;
                }
                var element=(FrameworkElement)owner.View.ContainerFromItem(anchor.Row);
                double top=element.TransformToVisual(owner.View).TransformPoint(new(0,0)).Y;
                target=scroll.VerticalOffset+top-anchor.Top;
            }
            target=Math.Clamp(target,0,Math.Max(0,scroll.ScrollableHeight));
            browserOffsetOwner=owner with{Offset=target};
            if(Math.Abs(scroll.VerticalOffset-target)>.5)scroll.ChangeView(null,target,null,true);
        }
        finally{applyingBrowserOffset=false;}
    }
    private void ReleaseUnseenPublicationRow(FileRow row)
    {
        if(!IsDecodingThumbnail(row)&&thumbnailRequests.Remove(row,out var request)){request.Cancel();request.Dispose();}
        if(propertyCancellations.TryGetValue(row,out var properties))properties.Cancel();
        row.Thumbnail=null;
    }
    private bool IsProtectedPublicationRow(FileRow row)=>browserOffsetOwner is {Anchor:{} anchor} owner
        &&ReferenceEquals(row,anchor.Row)&&OwnsBrowserOffset(owner)
        &&(firstPageRows.Contains(row)||results?.Contains(row)==true);
    private void ReleasePublicationRows()
    {
        if(publicationRows is not {} retained)return;publicationRows=null;
        foreach(var row in retained)
        {
            if(visible.Contains(row)||IsProtectedPublicationRow(row))continue;
            ReleaseUnseenPublicationRow(row);
        }
    }
}
