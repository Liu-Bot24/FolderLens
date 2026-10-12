using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed record ThumbnailMetadata(FileRow Row,SnapshotItem Item,ImageReply? Reply,MediaMetadata? Media,long RootVersion);
    private readonly Dictionary<FileRow,ThumbnailMetadata> pendingThumbnailMetadata=[];
    private bool thumbnailMetadataRunning;
    private CancellationTokenSource thumbnailMetadataStop=new();

    private void QueueThumbnailMetadata(FileRow row,ImageReply? reply=null,MediaMetadata? media=null)
    {
        if(closing||row.Item is not {} item)return;
        // This work is reconstructible from the source file. Keep only a bounded
        // visible backlog; a later visible demand can fill any omitted metadata.
        foreach(var stale in pendingThumbnailMetadata.Keys.Where(candidate=>!visible.Contains(candidate)).ToArray())pendingThumbnailMetadata.Remove(stale);
        if(pendingThumbnailMetadata.Count>=256&&!pendingThumbnailMetadata.ContainsKey(row))return;
        pendingThumbnailMetadata[row]=new(row,item,reply,media,rootChangeVersion);
        if(!thumbnailMetadataRunning)_=DrainThumbnailMetadata();
    }
    private async Task DrainThumbnailMetadata()
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        thumbnailMetadataRunning=true;
        try
        {
            while(!closing&&pendingThumbnailMetadata.Count>0)
            {
                var work=pendingThumbnailMetadata.Values.OrderByDescending(value=>HasThumbnailViewport(value.Row)).First();
                pendingThumbnailMetadata.Remove(work.Row);
                using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,thumbnailMetadataStop.Token);
                bool Current()=>!stop.IsCancellationRequested&&!closing&&work.RootVersion==rootChangeVersion&&SameThumbnailObservation(work.Item,work.Row.Item)
                    &&(firstPageRows.Contains(work.Row)||results?.Contains(work.Row)==true);
                if(!Current())continue;
                try
                {
                    if(verifyMetadataBarrier is not null)await verifyMetadataBarrier(stop.Token);
                    if(work.Media is {} info)
                    {
                        var item=work.Item;string sourceRoot=item.SourceRootId??rootId;long sourceEpoch=item.SourceRootEpoch??epoch;
                        await catalog!.ApplyMediaMetadata(item.EntryId,item.Version,sourceRoot,sourceEpoch,info,"ffprobe-v1",stop.Token);
                        if(info.Details is {} details)await catalog.ApplyFileDetails(item.EntryId,item.Version,sourceRoot,sourceEpoch,details,"ffprobe-v1",stop.Token);
                    }
                    else if(work.Reply is {} reply)await RecordMetadata(work.Row,reply,stop.Token);
                    else if(visible.Contains(work.Row))await ReadDemandedMetadata(work.Row,stop.Token);
                    if(Current()&&visible.Contains(work.Row))await ResolveRow(work.Row,rootId,stop.Token);
                }
                catch(OperationCanceledException) when(stop.IsCancellationRequested){}
                catch(Exception error){if(Current())ShowError(error);else RecordWebView("RetiredThumbnailMetadata: "+error.GetType().Name+" "+error.HResult);}
            }
        }
        finally{thumbnailMetadataRunning=false;}
    }
    private void CancelThumbnailMetadata()
    {
        pendingThumbnailMetadata.Clear();thumbnailMetadataStop.Cancel();thumbnailMetadataStop.Dispose();thumbnailMetadataStop=new();
    }
}
