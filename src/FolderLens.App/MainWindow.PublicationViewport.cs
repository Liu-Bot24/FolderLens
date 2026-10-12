using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private HashSet<FileRow>? publicationRows;
    private FileRow? retainedViewportAnchor;
    private bool keepBrowserStart=true;
    private long browserInteractionVersion;
    private bool restoringBrowserSelection;
    private void RestoreBrowserSelection(ListViewBase view,FileRow row)
    {
        bool previous=restoringBrowserSelection;restoringBrowserSelection=true;
        try{view.SelectedItem=row;}finally{restoringBrowserSelection=previous;}
    }
    private void ResetBrowserStart()
    {
        keepBrowserStart=true;retainedViewportAnchor=null;
    }
    private void BrowserPointerIntent(object sender,PointerRoutedEventArgs args)=>AcceptBrowserInteraction();
    private void BrowserKeyIntent(object sender,KeyRoutedEventArgs args)
    {
        if(args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
            AcceptBrowserInteraction();
    }
    private void AcceptBrowserInteraction()
    {
        if(closing||updatingBrowser)return;
        browserInteractionVersion++;keepBrowserStart=false;retainedViewportAnchor=null;
        pendingViewRestore=null;pendingPreviewRestore=null;
    }
    private void RestoreBrowserStart(ListViewBase view)
    {
        if(!keepBrowserStart||immersive)return;
        view.UpdateLayout();
        if(view.Items.Count>0)view.ScrollIntoView(view.Items[0],ScrollIntoViewAlignment.Leading);
        view.UpdateLayout();FindScrollViewer(view)?.ChangeView(null,0,null,true);
    }
    private (FileRow Row,double Top)? CapturePublicationViewport(ListViewBase view)
    {
        if(keepBrowserStart)return null;
        (FileRow Row,double Top)? anchor=null;double left=double.MaxValue;
        foreach(var pair in visibleContainers)
        {
            if(pair.Key.View!=view||pair.Key.Container is not FrameworkElement element||element.ActualHeight<=0)continue;
            var point=element.TransformToVisual(view).TransformPoint(new(0,0));
            if(point.Y+element.ActualHeight<=0||point.Y>=view.ActualHeight||point.X+element.ActualWidth<=0||point.X>=view.ActualWidth)continue;
            // Reuse the same surviving row across consecutive publications. Taking
            // an arbitrary row from a recycled container dictionary lets a stationary
            // viewport drift a row at a time when grid columns reflow.
            if(ReferenceEquals(pair.Value,retainedViewportAnchor))return(pair.Value,point.Y);
            if(anchor is null||point.Y<anchor.Value.Top||point.Y==anchor.Value.Top&&point.X<left){anchor=(pair.Value,point.Y);left=point.X;}
        }
        return anchor;
    }
    private void RestorePublicationViewport(ListViewBase view,(FileRow Row,double Top)? anchor)
    {
        if(keepBrowserStart){RestoreBrowserStart(view);return;}
        if(anchor is not {} saved||results?.Contains(saved.Row)!=true)return;
        retainedViewportAnchor=saved.Row;
        view.UpdateLayout();
        var element=view.ContainerFromItem(saved.Row) as FrameworkElement;
        if(element is null||Math.Abs(element.TransformToVisual(view).TransformPoint(new(0,0)).Y-saved.Top)>1)
        {
            view.ScrollIntoView(saved.Row,ScrollIntoViewAlignment.Leading);view.UpdateLayout();
            if(view.ContainerFromItem(saved.Row) is FrameworkElement restored&&FindScrollViewer(view) is {} scroll)
            {
                double top=restored.TransformToVisual(view).TransformPoint(new(0,0)).Y;
                scroll.ChangeView(null,Math.Max(0,scroll.VerticalOffset+top-saved.Top),null,true);view.UpdateLayout();
            }
        }
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
