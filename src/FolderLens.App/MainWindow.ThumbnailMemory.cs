using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    // Hold images, never rows/containers. Scrolling back reuses XAML's decoded
    // image without retaining the virtualized result set or reading the disk.
    private const long ThumbnailBitmapLimit=32L*1024*1024;
    private readonly Dictionary<string,LinkedListNode<(string Key,BitmapImage Image,long Bytes)>> thumbnailBitmaps=[];
    private readonly LinkedList<(string Key,BitmapImage Image,long Bytes)> recentThumbnailBitmaps=[];
    private long thumbnailBitmapBytes;

    private BitmapImage? FindThumbnailBitmap(ThumbnailCacheKey key)
    {
        if(!thumbnailBitmaps.TryGetValue(key.Hash(),out var node))return null;
        recentThumbnailBitmaps.Remove(node);recentThumbnailBitmaps.AddFirst(node);return node.Value.Image;
    }
    private void RememberThumbnailBitmap(ThumbnailCacheKey key,BitmapImage image)
    {
        if(WorkerResources.Shared.Snapshot.UnderPressure)return;
        long bytes=checked((long)image.PixelWidth*image.PixelHeight*4);
        if(bytes<1||bytes>ThumbnailBitmapLimit)return;
        string hash=key.Hash();
        if(thumbnailBitmaps.Remove(hash,out var old)){recentThumbnailBitmaps.Remove(old);thumbnailBitmapBytes-=old.Value.Bytes;}
        var node=recentThumbnailBitmaps.AddFirst((hash,image,bytes));thumbnailBitmaps.Add(hash,node);thumbnailBitmapBytes+=bytes;
        while(thumbnailBitmapBytes>ThumbnailBitmapLimit||thumbnailBitmaps.Count>512)
        {
            var last=recentThumbnailBitmaps.Last!;recentThumbnailBitmaps.RemoveLast();thumbnailBitmaps.Remove(last.Value.Key);thumbnailBitmapBytes-=last.Value.Bytes;
        }
    }
    private void ClearThumbnailBitmaps()
    {thumbnailBitmaps.Clear();recentThumbnailBitmaps.Clear();thumbnailBitmapBytes=0;}
}
