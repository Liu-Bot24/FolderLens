using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private static bool SameThumbnailObservation(SnapshotItem expected,SnapshotItem? current)=>current is not null
        &&expected.EntryId==current.EntryId&&expected.Version==current.Version&&expected.RelativePath==current.RelativePath
        &&expected.Bytes==current.Bytes&&expected.Kind==current.Kind&&expected.PathRevision==current.PathRevision
        &&expected.DirectoryLocationId==current.DirectoryLocationId&&expected.BindingRevision==current.BindingRevision
        &&expected.PhysicalIdentity==current.PhysicalIdentity&&expected.SourceRootId==current.SourceRootId
        &&expected.SourceRootPath==current.SourceRootPath&&expected.SourceRootEpoch==current.SourceRootEpoch;
    private readonly HashSet<ListViewBase> thumbnailViews=[];
    private readonly Dictionary<ListViewBase,ScrollViewer> thumbnailScrollers=[];
    private readonly Dictionary<FileRow,CancellationTokenSource> decodingThumbnails=[];
    private bool IsDecodingThumbnail(FileRow row)=>decodingThumbnails.TryGetValue(row,out var decoderOwner)
        &&thumbnailRequests.TryGetValue(row,out var current)&&ReferenceEquals(decoderOwner,current);
    private bool thumbnailDispatchQueued;

    private void ObserveThumbnailViewport(ListViewBase view)
    {
        if(thumbnailViews.Add(view))
        {
            view.LayoutUpdated+=ThumbnailLayoutUpdated;
            view.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler(BrowserPointerIntent),true);
            view.AddHandler(UIElement.PointerWheelChangedEvent,new PointerEventHandler(BrowserWheelIntent),true);
            view.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler(BrowserPointerReleased),true);
            view.AddHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler(BrowserPointerReleased),true);
            view.AddHandler(UIElement.KeyDownEvent,new KeyEventHandler(BrowserKeyIntent),true);
        }
        if(FindScrollViewer(view) is not {} scroll||thumbnailScrollers.GetValueOrDefault(view)==scroll)return;
        if(thumbnailScrollers.TryGetValue(view,out var previous))previous.ViewChanged-=ThumbnailViewChanged;
        thumbnailScrollers[view]=scroll;scroll.ViewChanged+=ThumbnailViewChanged;
    }
    private void ThumbnailLayoutUpdated(object? sender,object args){QueueBrowserOffset();QueueThumbnailViewport();}
    private void ThumbnailViewChanged(object? sender,ScrollViewerViewChangedEventArgs args)
    {
        if(ReferenceEquals(sender,FindScrollViewer(ActiveBrowser))&&browserMovementPending)ContinueBrowserMovement();
        QueueBrowserOffset();QueueThumbnailViewport();
    }
    private bool HasThumbnailViewport(FileRow row)=>visibleContainers.Any(pair=>ReferenceEquals(pair.Value,row)&&IsThumbnailInViewport(pair.Key.View,row));
    private void QueueThumbnailViewport()
    {
        if(closing||thumbnailDispatchQueued)return;
        thumbnailDispatchQueued=true;
        if(!DispatcherQueue.TryEnqueue(() =>
        {
            thumbnailDispatchQueued=false;
            if(closing||updatingBrowser)return;
            UpdateCurrentFolderHeader();
            foreach(var view in thumbnailViews.ToArray())ObserveThumbnailViewport(view);
            foreach(var pair in thumbnailRequests.ToArray())
                // Do not kill a decoder on every wheel tick. Retire queued work;
                // an in-flight image completes and the next admission uses the new view.
                if(!IsDecodingThumbnail(pair.Key)&&!HasThumbnailViewport(pair.Key))pair.Value.Cancel();
            foreach(var pair in propertyCancellations.ToArray())if(!HasThumbnailViewport(pair.Key))pair.Value.Cancel();
            var demanded=visibleContainers.Where(pair=>IsThumbnailInViewport(pair.Key.View,pair.Value))
                .GroupBy(pair=>pair.Value).Select(group=>group.First()).OrderBy(pair=>pair.Value.Ordinal).ToArray();
            foreach(var pair in demanded)
            {
                if(thumbnailRequests.Count+propertyRequests.Count>=1024)break;
                _=LoadThumbnail(pair.Key.View,pair.Value);
            }
        }))thumbnailDispatchQueued=false;
    }
    private void DetachThumbnailViewports()
    {
        foreach(var view in thumbnailViews)
        {
            view.LayoutUpdated-=ThumbnailLayoutUpdated;
            view.RemoveHandler(UIElement.PointerPressedEvent,new PointerEventHandler(BrowserPointerIntent));
            view.RemoveHandler(UIElement.PointerWheelChangedEvent,new PointerEventHandler(BrowserWheelIntent));
            view.RemoveHandler(UIElement.PointerReleasedEvent,new PointerEventHandler(BrowserPointerReleased));
            view.RemoveHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler(BrowserPointerReleased));
            view.RemoveHandler(UIElement.KeyDownEvent,new KeyEventHandler(BrowserKeyIntent));
        }
        foreach(var scroll in thumbnailScrollers.Values)scroll.ViewChanged-=ThumbnailViewChanged;
        thumbnailViews.Clear();thumbnailScrollers.Clear();realizedFolderHeaders.Clear();browserMovementTimer?.Stop();SetBrowserOffsetOwner(null);
    }

    private async Task EnsureCurrentRowLoaded(FileRow row,CancellationToken token,bool foreground=false)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            if(firstPageRows.Contains(row)&&row.Item is not null)return;
            var source=results??throw new OperationCanceledException("当前列表已关闭。",token);
            if(!source.Contains(row))throw new OperationCanceledException("文件已离开当前列表。",token);
            try{await source.EnsureLoaded(row,token,foreground);return;}
            catch(OperationCanceledException) when(!token.IsCancellationRequested&&!closing&&results is {} current
                &&!ReferenceEquals(current,source)&&current.Contains(row))
            {
                // The same file/placeholder survived publication. Only the old
                // source retired; keep the current user's selection request alive.
            }
        }
    }
}
