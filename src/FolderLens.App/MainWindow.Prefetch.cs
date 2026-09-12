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
    private readonly LinkedList<PrefetchedImage> prefetched=[];
    private long prefetchBytes;
    private readonly LinkedList<PrefetchedDetail> prefetchedDetails=[];
    private long prefetchedDetailBytes;
    private sealed record PrefetchedDetail(string Root,long Epoch,string Path,long Version,long Modified,long Length,int X,int Y,byte[] Png);
    private readonly SourceFileProbe prefetchSourceProbe=new();
    private sealed record PrefetchedImage(string Root,long Epoch,string Path,long Version,long Modified,long Length,int TargetWidth,int TargetHeight,byte[] Png,WorkerEnvelope Message);
    private async Task<PrefetchedImage?> FindPrefetched(FileRow row,int width,int height,CancellationToken token)
    {
        string path=Path.Combine(root,row.RelativePath);
        var cached=prefetched.FirstOrDefault(item=>item.Root==rootId&&item.Epoch==epoch&&item.Path==path&&item.Version==row.Item!.Version&&item.TargetWidth>=width&&item.TargetHeight>=height);
        if(cached is null)return null;
        var stat=await prefetchSourceProbe.Read(path,token,approvedCloud.Contains(CloudKey(row)));
        bool current=stat.Length==cached.Length&&stat.ModifiedUtcTicks==cached.Modified;
        if(!current){prefetched.Remove(cached);prefetchBytes-=cached.Png.Length;return null;}
        if(Environment.GetCommandLineArgs().Contains("--diagnostic-ui"))RecordWebView($"Prefetch hit ordinal={row.Ordinal}");
        return cached;
    }
    private async Task PresentPrefetched(PrefetchedImage cached,long current,CancellationToken token)
    {
        using var bytes=new MemoryStream(cached.Png,false);using var random=bytes.AsRandomAccessStream();var bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,random);
        if(current!=selection||token.IsCancellationRequested){bitmap.Dispose();return;}
        fitBitmap?.Dispose();fitBitmap=bitmap;sourceWidth=cached.Message.Metadata!.Value.GetProperty("width").GetInt32();sourceHeight=cached.Message.Metadata.Value.GetProperty("height").GetInt32();rawPreviewOnly=cached.Message.Quality=="rawEmbedded";ApplyViewerSizing();
    }
    private void SchedulePrefetch(long current)
    {
        prefetchStop.Cancel();prefetchStop.Dispose();prefetchStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token,lifetime.Token);
        var token=prefetchStop.Token;prefetchTask=PrefetchNeighbours(current,token);
    }
    private async Task PrefetchNeighbours(long current,CancellationToken token)
    {
        if(selected is null||results is null||prefetchWorker is null)return;
        var sourceResults=results;long ordinal=selected.Ordinal;string sourceRoot=root,sourceRootId=rootId;long sourceEpoch=epoch,sourceGeneration=generation;
        // A sidebar-sized preload is insufficient when the next action opens full screen.
        int width=Math.Max(256,(int)(Shell.ActualWidth*Shell.XamlRoot.RasterizationScale)),height=Math.Max(256,(int)(Shell.ActualHeight*Shell.XamlRoot.RasterizationScale));
        var detailTargets=new List<(FileRow Row,int Width,int Height)>();
        try
        {
            foreach(long index in new[]{ordinal+1,ordinal-1})
            {
                token.ThrowIfCancellationRequested();if(index<0||index>=sourceResults.Count)continue;
                var row=(FileRow)sourceResults[(int)index]!;await sourceResults.EnsureLoaded(row,token);if(row.Kind!="image")continue;
                bool missingProperties=Stamp(row) is null;
                var properties=await ResolveRow(row,sourceRootId,token);
                if(current!=selection||sourceRootId!=rootId||sourceEpoch!=epoch||sourceGeneration!=generation||sourceResults!=results||token.IsCancellationRequested)return;
                if(properties.HydrationState=="placeholder"&&!approvedCloud.Contains(CloudKey(row)))continue;
                if(Stamp(row) is not {} expectedStamp)continue;
                if(await FindPrefetched(row,width,height,token) is {} ready)
                {detailTargets.Add((row,ready.Message.Metadata!.Value.GetProperty("width").GetInt32(),ready.Message.Metadata.Value.GetProperty("height").GetInt32()));continue;}
                string path=Path.Combine(sourceRoot,row.RelativePath);ImageReply? reply=null;
                try
                {
                    bool raw=FileKinds.Raw.Contains(Path.GetExtension(path));reply=await prefetchWorker.Request(path,raw?"rawEmbedded":"fit",new(sourceRootId,sourceEpoch,sourceGeneration,current,row.Item!.Version,1),new(width,height),token,expectedStamp);
                    if(reply.Message.Metadata!.Value.GetProperty("isAnimated").GetBoolean())continue;
                    long encodedLength=new FileInfo(reply.AssetPath!).Length;if(encodedLength>32L*1024*1024)continue;
                    byte[] png=await File.ReadAllBytesAsync(reply.AssetPath!,token);
                    if(current!=selection||sourceRootId!=rootId||sourceEpoch!=epoch||sourceGeneration!=generation||sourceResults!=results||token.IsCancellationRequested)return;
                    var cached=new PrefetchedImage(sourceRootId,sourceEpoch,path,row.Item.Version,expectedStamp.ModifiedUtcTicks,expectedStamp.Length,width,height,png,reply.Message);
                    prefetched.AddFirst(cached);prefetchBytes+=png.Length;
                    if(Environment.GetCommandLineArgs().Contains("--diagnostic-ui"))RecordWebView($"Prefetch ready ordinal={row.Ordinal} resolvedMissingProperties={missingProperties}");
                    while(prefetched.Count>2||prefetchBytes>32L*1024*1024){prefetchBytes-=prefetched.Last!.Value.Png.Length;prefetched.RemoveLast();}
                    if(!raw)detailTargets.Add((row,reply.Message.Metadata.Value.GetProperty("width").GetInt32(),reply.Message.Metadata.Value.GetProperty("height").GetInt32()));
                }
                finally{if(reply is not null)await prefetchWorker.ReleaseAsset(reply);}
            }
            // Adjacent fit previews get priority. Then warm a bounded central detail region.
            if(current==selection&&selected is {} active&&AnimationButton.Visibility!=Visibility.Visible&&imagePage==0&&!rawPreviewOnly)
                detailTargets.Insert(0,(active,(int)sourceWidth,(int)sourceHeight));
            foreach(var target in detailTargets)
            {
                token.ThrowIfCancellationRequested();if(current!=selection||sourceResults!=results)return;
                await WarmPressDetails(target.Row,target.Width,target.Height,current,token);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!closing)RecordWebView("Prefetch skipped: "+ex.GetType().Name);}
    }
    private async Task WarmPressDetails(FileRow row,int width,int height,long current,CancellationToken token)
    {
        if(width<=0||height<=0||Stamp(row) is not {} stamp||FileKinds.Raw.Contains(Path.GetExtension(row.RelativePath)))return;
        string path=Path.Combine(root,row.RelativePath),id=rootId;long activeEpoch=epoch;
        for(int y=Math.Max(0,height/2-512)/1024;y<=Math.Min(height-1,height/2+511)/1024;y++)
        for(int x=Math.Max(0,width/2-512)/1024;x<=Math.Min(width-1,width/2+511)/1024;x++)
        {
            if(prefetchedDetails.Any(item=>item.Root==id&&item.Epoch==activeEpoch&&item.Path==path&&item.Version==row.Item!.Version&&item.Modified==stamp.ModifiedUtcTicks&&item.Length==stamp.Length&&item.X==x&&item.Y==y))continue;
            ImageReply? reply=null;
            try
            {
                reply=await prefetchWorker!.Request(path,"fullTile",new(id,activeEpoch,generation,current,row.Item!.Version,1),new(1024,1024,TileX:x,TileY:y),token,stamp);
                if(new FileInfo(reply.AssetPath!).Length>8L*1024*1024)continue;
                byte[] bytes=await File.ReadAllBytesAsync(reply.AssetPath!,token);
                if(current!=selection||rootId!=id||epoch!=activeEpoch||token.IsCancellationRequested)return;
                prefetchedDetails.AddFirst(new PrefetchedDetail(id,activeEpoch,path,row.Item.Version,stamp.ModifiedUtcTicks,stamp.Length,x,y,bytes));prefetchedDetailBytes+=bytes.Length;
                while(prefetchedDetails.Count>12||prefetchedDetailBytes>32L*1024*1024){prefetchedDetailBytes-=prefetchedDetails.Last!.Value.Png.Length;prefetchedDetails.RemoveLast();}
            }
            finally{if(reply is not null)await prefetchWorker!.ReleaseAsset(reply);}
        }
    }
    private async Task<CanvasBitmap?> ReadPrefetchedDetail(FileRow row,(int X,int Y) key,CancellationToken token)
    {
        if(Stamp(row) is not {} stamp)return null;string path=Path.Combine(root,row.RelativePath);
        var cached=prefetchedDetails.FirstOrDefault(item=>item.Root==rootId&&item.Epoch==epoch&&item.Path==path&&item.Version==row.Item!.Version&&item.Modified==stamp.ModifiedUtcTicks&&item.Length==stamp.Length&&item.X==key.X&&item.Y==key.Y);
        if(cached is null)return null;token.ThrowIfCancellationRequested();
        using var stream=new MemoryStream(cached.Png,false);using var random=stream.AsRandomAccessStream();
        return await CanvasBitmap.LoadAsync(ImageCanvas,random);
    }
}
