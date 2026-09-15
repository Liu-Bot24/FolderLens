using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyCacheRecovery(byte[] png,Dictionary<string,object> report)
    {
        string source=Path.Combine(dataDirectory,"generated-valid.png"),bad=Path.Combine(dataDirectory,"generated-bad.png");await File.WriteAllBytesAsync(source,png);
        byte[] broken=(byte[])png.Clone();Array.Fill<byte>(broken,0,41,Math.Max(0,broken.Length-53));await File.WriteAllBytesAsync(bad,broken);
        var key=new ThumbnailCacheKey("generated-bad-png",1,1,png.Length,256,"verify");
        using(await thumbnailCache!.Store(key,bad)){}
        async Task<BitmapImage> Decode(ThumbnailCacheLease lease){using var stream=lease.OpenRead();var image=new BitmapImage();await image.SetSourceAsync(stream.AsRandomAccessStream());return image;}
        using var otherReader=await thumbnailCache.TryGet(key);
        var result=await thumbnailCache.TryLoad(key,Decode,lifetime.Token);
        if(result is not null)throw new InvalidOperationException("损坏缓存被当作正常图片。");
        using(var stale=await thumbnailCache.TryGet(key)){if(stale is not null)throw new InvalidOperationException("损坏缓存未清退。");}
        var blocked=await thumbnailCache.StoreOptional(key,source);
        if(blocked.Lease is not null)throw new InvalidOperationException("仍被使用的坏缓存被重新交给解码器。");
        otherReader?.Dispose();
        using(await thumbnailCache.Store(key,source)){}
        if(await thumbnailCache.TryLoad(key,Decode,lifetime.Token) is null)throw new InvalidOperationException("正确源图片无法恢复缓存。");
        try{await thumbnailCache.TryLoad<BitmapImage>(key,_=>throw new OutOfMemoryException(),lifetime.Token);throw new InvalidOperationException("资源错误被吞掉。");}catch(OutOfMemoryException){}
        using(var kept=await thumbnailCache.TryGet(key)){if(kept is null)throw new InvalidOperationException("资源不足误删了正常缓存。");}
        report["badPngEvictedAndRebuilt"]=true;report["resourceFailureRetainsCache"]=true;report["status"]="PASS";
    }
}
