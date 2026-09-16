using System.Numerics;
using FolderLens.Contracts;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private WorkerClient? prefetchWorker;
    private CancellationTokenSource prefetchStop=new();
    private Task? prefetchTask;
    private PendingImagePrefetch? pendingImagePrefetch;
    private int prefetchDirection=1;
    private sealed record PendingImagePrefetch(FileRow Row,string Root,long Epoch,long Generation,int Width,int Height,CancellationToken Token)
    {
        public TaskCompletionSource Completed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PixelsDecoded;
    }
    // A low-priority decode may be slower than a new foreground request. Adopt only
    // after decoding has finished; file transfer/bitmap preparation can still be pending.
    private bool CanAdoptPrefetch(FileRow row)=>pendingImagePrefetch is {PixelsDecoded:true} pending&&!pending.Token.IsCancellationRequested&&ReferenceEquals(pending.Row,row)&&pending.Root==rootId&&pending.Epoch==epoch&&pending.Generation==generation;
    private readonly LinkedList<PrefetchedImage> prefetched=[];
    private long prefetchBytes;
    private long preparedPrefetchBytes;
    private readonly SemaphoreSlim preparedPrefetchGate=new(1,1);
    private long preparingPrefetchBytes;
    private const long PreparedPrefetchLimit=64L*1024*1024;
    private readonly LinkedList<PrefetchedDetail> prefetchedDetails=[];
    private long prefetchedDetailBytes;
    private int prefetchPressurePending;
    private void OnPrefetchMemoryPressure()
    {
        if(Interlocked.Exchange(ref prefetchPressurePending,1)!=0)return;
        if(!DispatcherQueue.TryEnqueue(()=>
        {
            try
            {
                if(closing)return;
                prefetchStop.Cancel();ClearPrefetchedImages();prefetchedDetails.Clear();prefetchedDetailBytes=0;
            }
            finally{Volatile.Write(ref prefetchPressurePending,0);}
        }))Volatile.Write(ref prefetchPressurePending,0);
    }
    private sealed record PrefetchedDetail(string Root,long Epoch,string Path,long Version,SourceFileStamp Stamp,int X,int Y,byte[] Png);
    private readonly SourceFileProbe prefetchSourceProbe=new();
    private sealed record PrefetchedImage(string Root,long Epoch,string Path,long Version,SourceFileStamp Stamp,int TargetWidth,int TargetHeight,byte[] Png,WorkerEnvelope Message)
    {
        public CanvasBitmap? Bitmap;
        public long DeviceRevision;
        public long BitmapBytes;
    }
    private void ReleasePreparedImage(PrefetchedImage image)
    {
        image.Bitmap?.Dispose();image.Bitmap=null;preparedPrefetchBytes-=image.BitmapBytes;image.BitmapBytes=0;
    }
    private void RemovePrefetchedImage(PrefetchedImage image)
    {
        if(!prefetched.Remove(image))return;prefetchBytes-=image.Png.Length;ReleasePreparedImage(image);
    }
    private void ClearPrefetchedImages()
    {
        foreach(var image in prefetched)ReleasePreparedImage(image);
        prefetched.Clear();prefetchBytes=0;
    }
    private async Task PreparePrefetchedImage(PrefetchedImage image,CancellationToken token)
    {
        await preparedPrefetchGate.WaitAsync(token);
        try
        {
        token.ThrowIfCancellationRequested();
        if(closing||!prefetched.Contains(image))return;
        if(verifyEncodedPrefetch||image.Bitmap is not null||WorkerResources.Shared.Snapshot.UnderPressure)return;
        long reserve=checked((long)image.TargetWidth*image.TargetHeight*4);
        if(reserve>PreparedPrefetchLimit)return;
        foreach(var old in prefetched.Reverse())
        {if(preparedPrefetchBytes+reserve<=PreparedPrefetchLimit)break;if(!ReferenceEquals(old,image))ReleasePreparedImage(old);}
        if(preparedPrefetchBytes+reserve>PreparedPrefetchLimit)return;
        preparingPrefetchBytes=reserve;
        long deviceRevision=imageResourceRevision;
        using var bytes=new MemoryStream(image.Png,false);using var random=bytes.AsRandomAccessStream();
        var bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,random);
        if(token.IsCancellationRequested||closing||deviceRevision!=imageResourceRevision||!prefetched.Contains(image)||WorkerResources.Shared.Snapshot.UnderPressure)
        {bitmap.Dispose();return;}
        long cost=checked((long)bitmap.SizeInPixels.Width*bitmap.SizeInPixels.Height*4);
        if(preparedPrefetchBytes+cost>PreparedPrefetchLimit){bitmap.Dispose();return;}
        image.Bitmap=bitmap;image.BitmapBytes=cost;image.DeviceRevision=deviceRevision;preparedPrefetchBytes+=cost;
        }
        finally{preparingPrefetchBytes=0;preparedPrefetchGate.Release();}
    }
    private async Task<PrefetchedImage?> FindPrefetched(FileRow row,int width,int height,CancellationToken token)
    {
        string path=SourcePath(row);
        if(CanAdoptPrefetch(row)&&pendingImagePrefetch is {} pending&&pending.Width>=width&&pending.Height>=height)
        {
            try{await pending.Completed.Task.WaitAsync(TimeSpan.FromMilliseconds(200),token);}
            catch(TimeoutException){token.ThrowIfCancellationRequested();if(ReferenceEquals(pendingImagePrefetch,pending))prefetchStop.Cancel();}
        }
        var cached=prefetched.FirstOrDefault(item=>item.Root==rootId&&item.Epoch==epoch&&item.Path==path&&item.Version==row.Item!.Version&&item.TargetWidth>=width&&item.TargetHeight>=height);
        if(cached is null)
        {
            if(verifyPreviewStage is not null&&ReferenceEquals(row,selected))
            {
                var candidate=prefetched.FirstOrDefault(item=>item.Path==path);
                verifyPreviewStage(candidate is null?"cacheAbsent":$"cacheMismatch version={candidate.Version}/{row.Item!.Version} size={candidate.TargetWidth}x{candidate.TargetHeight}/{width}x{height} epoch={candidate.Epoch}/{epoch}");
            }
            return null;
        }
        var stat=await prefetchSourceProbe.Read(path,token,approvedCloud.Contains(CloudKey(row)));
        bool current=stat==cached.Stamp;
        if(!current){RemovePrefetchedImage(cached);return null;}
        if(prefetched.Remove(cached))prefetched.AddFirst(cached);
        if(Environment.GetCommandLineArgs().Contains("--diagnostic-ui"))RecordWebView($"Prefetch hit ordinal={row.Ordinal}");
        return cached;
    }
    private async Task PresentPrefetched(PrefetchedImage cached,long current,CancellationToken token)
    {
        CanvasBitmap bitmap;
        if(cached.Bitmap is {} prepared&&cached.DeviceRevision==imageResourceRevision)
        {
            // Ownership moves to the displayed image; cache eviction must never dispose it.
            bitmap=prepared;cached.Bitmap=null;preparedPrefetchBytes-=cached.BitmapBytes;cached.BitmapBytes=0;
            verifyPreviewStage?.Invoke("preparedBitmapHit");
        }
        else
        {
            ReleasePreparedImage(cached);
            using var bytes=new MemoryStream(cached.Png,false);using var random=bytes.AsRandomAccessStream();bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,random);
        }
        if(current!=selection||token.IsCancellationRequested){bitmap.Dispose();return;}
        fitBitmap?.Dispose();fitBitmap=bitmap;verifyBitmapSelection=current;sourceWidth=cached.Message.Metadata!.Value.GetProperty("width").GetInt32();sourceHeight=cached.Message.Metadata.Value.GetProperty("height").GetInt32();rawPreviewOnly=cached.Message.Quality=="rawEmbedded";ApplyViewerSizing();
    }
    private void SchedulePrefetch(long current)
    {
        if(verifyColdSwitch)return;
        prefetchStop.Cancel();prefetchStop.Dispose();prefetchStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token=prefetchStop.Token;prefetchTask=PrefetchNeighbours(current,token);
    }
    private async Task PrefetchNeighbours(long current,CancellationToken token)
    {
        using var work=browserWork.Enter();if(work is null||closing)return;
        if(selected is null||results is null||prefetchWorker is null||WorkerResources.Shared.Snapshot.UnderPressure)return;
        var sourceResults=results;long ordinal=selected.Ordinal;string sourceRoot=root,sourceRootId=rootId;long sourceEpoch=epoch,sourceGeneration=generation;
        // Prefetch for the current display size; enlarging the viewer requests more pixels on demand.
        int width=Math.Max(256,(int)(ImageCanvas.ActualWidth*Shell.XamlRoot.RasterizationScale)),height=Math.Max(256,(int)(ImageCanvas.ActualHeight*Shell.XamlRoot.RasterizationScale));

        try
        {
            foreach(long index in new[]{ordinal+prefetchDirection,ordinal-prefetchDirection})
            {
                token.ThrowIfCancellationRequested();if(index<0||index>=sourceResults.Count)continue;
                var row=(FileRow)sourceResults[(int)index]!;await sourceResults.EnsureLoaded(row,token);if(row.Kind!="image")continue;
                bool missingProperties=Stamp(row) is null;
                var properties=await ResolveRow(row,sourceRootId,token);
                if(current!=selection||sourceRootId!=rootId||sourceEpoch!=epoch||sourceGeneration!=generation||sourceResults!=results||token.IsCancellationRequested)return;
                if(properties.HydrationState=="placeholder"&&!approvedCloud.Contains(CloudKey(row)))continue;
                if(Stamp(row) is not {} expectedStamp)continue;
                var observation=row.Item!;
                bool RowStillCurrent()=>sourceResults==results||results?.IndexOf(row)>=0&&row.Item is {} item&&SameCollectionObservation(item,observation);
                if(await FindPrefetched(row,width,height,token) is {} ready)
                {await PreparePrefetchedImage(ready,token);continue;}
                string path=SourcePath(row);ImageReply? reply=null;
                var pending=new PendingImagePrefetch(row,sourceRootId,sourceEpoch,sourceGeneration,width,height,token);pendingImagePrefetch=pending;
                try
                {
                    // Start the isolated source probe while the image is still background work.
                    // Selection validates again: this warm-up does not authorize stale pixels.
                    var sourceStat=await prefetchSourceProbe.Read(path,token,approvedCloud.Contains(CloudKey(row)));
                    if(sourceStat!=expectedStamp)continue;
                    bool raw=FileKinds.Raw.Contains(Path.GetExtension(path));reply=await prefetchWorker.Request(path,raw?"rawEmbedded":"fit",new(sourceRootId,sourceEpoch,sourceGeneration,current,row.Item!.Version,1),new(width,height),token,expectedStamp,approvedCloud.Contains(CloudKey(row)));
                    pending.PixelsDecoded=true;
                    if(verifyPrefetchBarrier is not null)await verifyPrefetchBarrier(row,token);
                    if(reply.Message.Metadata!.Value.GetProperty("isAnimated").GetBoolean())continue;
                    long encodedLength=new FileInfo(reply.AssetPath!).Length;if(encodedLength>32L*1024*1024)continue;
                    var metadata=reply.Message.Metadata!.Value;
                    if(metadata.GetProperty("pages").GetInt32()>1&&!metadata.GetProperty("isAnimated").GetBoolean()&&!metadata.GetProperty("isRaw").GetBoolean())continue;
                    byte[] png=await File.ReadAllBytesAsync(reply.AssetPath!,token);
                    if((current!=selection&&!ReferenceEquals(row,selected))||sourceRootId!=rootId||sourceEpoch!=epoch||sourceGeneration!=generation||!RowStillCurrent()||token.IsCancellationRequested)return;
                    var cached=new PrefetchedImage(sourceRootId,sourceEpoch,path,row.Item.Version,expectedStamp,width,height,png,reply.Message);
                    prefetched.AddFirst(cached);prefetchBytes+=png.Length;
                    if(Environment.GetCommandLineArgs().Contains("--diagnostic-ui"))RecordWebView($"Prefetch ready ordinal={row.Ordinal} resolvedMissingProperties={missingProperties}");
                    while(prefetched.Count>2||prefetchBytes>32L*1024*1024)RemovePrefetchedImage(prefetched.Last!.Value);
                    await PreparePrefetchedImage(cached,token);

                }
                finally
                {
                    try{if(reply is not null)await prefetchWorker.ReleaseAsset(reply);}
                    finally{if(ReferenceEquals(pendingImagePrefetch,pending))pendingImagePrefetch=null;pending.Completed.TrySetResult();}
                }
                // An incremental publication can retain this row, but its old
                // ordinal sequence must not be used to prefetch another neighbour.
                if(sourceResults!=results)return;
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!closing)RecordWebView("Prefetch skipped: "+ex.GetType().Name);}
    }
    private async Task WarmPressDetails(FileRow row,int width,int height,long current,CancellationToken token)
    {
        if(width<=0||height<=0||Stamp(row) is not {} stamp||FileKinds.Raw.Contains(Path.GetExtension(row.RelativePath)))return;
        string path=SourcePath(row),id=rootId;long activeEpoch=epoch;
        for(int y=Math.Max(0,height/2-512)/1024;y<=Math.Min(height-1,height/2+511)/1024;y++)
        for(int x=Math.Max(0,width/2-512)/1024;x<=Math.Min(width-1,width/2+511)/1024;x++)
        {
            if(prefetchedDetails.Any(item=>item.Root==id&&item.Epoch==activeEpoch&&item.Path==path&&item.Version==row.Item!.Version&&item.Stamp==stamp&&item.X==x&&item.Y==y))continue;
            ImageReply? reply=null;
            try
            {
                reply=await prefetchWorker!.Request(path,"fullTile",new(id,activeEpoch,generation,current,row.Item!.Version,1),new(1024,1024,TileX:x,TileY:y),token,stamp,approvedCloud.Contains(CloudKey(row)));
                if(new FileInfo(reply.AssetPath!).Length>8L*1024*1024)continue;
                byte[] bytes=await File.ReadAllBytesAsync(reply.AssetPath!,token);
                if(current!=selection||rootId!=id||epoch!=activeEpoch||token.IsCancellationRequested)return;
                prefetchedDetails.AddFirst(new PrefetchedDetail(id,activeEpoch,path,row.Item.Version,stamp,x,y,bytes));prefetchedDetailBytes+=bytes.Length;
                while(prefetchedDetails.Count>12||prefetchedDetailBytes>32L*1024*1024){prefetchedDetailBytes-=prefetchedDetails.Last!.Value.Png.Length;prefetchedDetails.RemoveLast();}
            }
            finally{if(reply is not null)await prefetchWorker!.ReleaseAsset(reply);}
        }
    }
    private async Task<CanvasBitmap?> ReadPrefetchedDetail(FileRow row,(int X,int Y) key,CancellationToken token)
    {
        if(Stamp(row) is not {} stamp)return null;string path=SourcePath(row);
        var cached=prefetchedDetails.FirstOrDefault(item=>item.Root==rootId&&item.Epoch==epoch&&item.Path==path&&item.Version==row.Item!.Version&&item.Stamp==stamp&&item.X==key.X&&item.Y==key.Y);
        if(cached is null)return null;token.ThrowIfCancellationRequested();
        if(await prefetchSourceProbe.Read(path,token,approvedCloud.Contains(CloudKey(row)))!=cached.Stamp)
        {prefetchedDetails.Remove(cached);prefetchedDetailBytes-=cached.Png.Length;return null;}
        using var stream=new MemoryStream(cached.Png,false);using var random=stream.AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(ImageCanvas,random);
    }
}
