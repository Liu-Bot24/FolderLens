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
    private long browserInteractionVersion;
    private bool restoringBrowserSelection;
    private sealed record BrowserOffsetOwner(ListViewBase View,object Source,long RootVersion,long InteractionVersion,double Offset);
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
        keepBrowserStart=true;browserOffsetOwner=null;browserMovementPending=false;browserScrollPointer=null;browserMovementTimer?.Stop();viewportPublicationPending=false;
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
        browserInteractionVersion++;pendingViewRestore=null;pendingPreviewRestore=null;
        if(changesViewport)BeginBrowserMovement();
        else if(browserOffsetOwner is {} owner)browserOffsetOwner=owner with{InteractionVersion=browserInteractionVersion};
    }
    private void BeginBrowserMovement()
    {
        browserOffsetOwner=null;keepBrowserStart=false;browserMovementPending=true;ContinueBrowserMovement();
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
    private void RestorePublicationViewport(ListViewBase view,double offset)
    {
        if(closing||immersive||browserMovementPending||view.ItemsSource is not {} source)return;
        offset=double.IsFinite(offset)?Math.Max(0,offset):0;
        keepBrowserStart=offset<=1;
        browserOffsetOwner=new(view,source,rootChangeVersion,browserInteractionVersion,offset);
        // Keep the numeric offset through later native layout/anchoring passes.
        // Never ScrollIntoView an old file just because a batch precedes it.
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
        if(!OwnsBrowserOffset(owner)){browserOffsetOwner=null;return;}
        if(FindScrollViewer(owner.View) is not {} scroll)return;
        double target=Math.Clamp(owner.Offset,0,Math.Max(0,scroll.ScrollableHeight));
        if(Math.Abs(scroll.VerticalOffset-target)<=.5)return;
        applyingBrowserOffset=true;
        try{scroll.ChangeView(null,target,null,true);}
        finally{applyingBrowserOffset=false;}
    }
    private void ReleasePublicationRows()
    {
        if(publicationRows is not {} retained)return;publicationRows=null;
        foreach(var row in retained)
        {
            if(visible.Contains(row))continue;
            if(!IsDecodingThumbnail(row)&&thumbnailRequests.Remove(row,out var request)){request.Cancel();request.Dispose();}
            row.Thumbnail=null;
        }
    }
}
