using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyBitmapAssets(string directory,Dictionary<string,object> report)
    {
        using var metadata=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"bitmap-assets.json")));
        int width=metadata.RootElement.GetProperty("width").GetInt32(),height=metadata.RootElement.GetProperty("height").GetInt32();
        long expected=checked((long)width*height*4);
        if(width<1||height<1||expected>64L*1024*1024)throw new InvalidDataException("Diagnostic bitmap exceeds budget.");
        string png=Path.Combine(directory,"mode-0.png"),raw=Path.Combine(directory,"mode-3.bgra");
        if(new FileInfo(raw).Length!=expected)throw new InvalidDataException("Invalid diagnostic pixel length.");
        byte[] pixels=await File.ReadAllBytesAsync(raw);
        var lifetimeChecks=new List<object>();report["fileLifetime"]=lifetimeChecks;
        foreach(string mode in new[]{"path","closedOperation","nativeStream"})
        {
            string owned=Path.Combine(dataDirectory,mode+"-lifetime.png");File.Copy(png,owned);
            CanvasBitmap bitmap;
            if(mode=="nativeStream")
            {
                bitmap=await LoadLocalBitmap(owned);
            }
            else
            {
                var operation=CanvasBitmap.LoadAsync(ImageCanvas,owned);bitmap=await operation;
                if(mode=="closedOperation")operation.Close();
            }
            using(bitmap)
            {
                bool released;string? error=null;
                try{File.Delete(owned);released=true;}catch(IOException ex){released=false;error=ex.GetType().Name+":"+ex.HResult;}
                lifetimeChecks.Add(new{mode,released,error,pixelsValid=bitmap.GetPixelBytes().Length==expected});
                if(mode=="nativeStream"&&!released)throw new IOException("Native stream retained the worker asset after loading.");
            }
        }
        using(var reference=await CanvasBitmap.LoadAsync(ImageCanvas,png))
        using(var direct=CanvasBitmap.CreateFromBytes(ImageCanvas,pixels,width,height,DirectXPixelFormat.B8G8R8A8UIntNormalized))
        {
            if(!SHA256.HashData(reference.GetPixelBytes()).SequenceEqual(SHA256.HashData(direct.GetPixelBytes())))throw new InvalidDataException("Displayed pixel formats differ.");
            using var stream=new FileStream(png,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            using var random=stream.AsRandomAccessStream();using var adapted=await CanvasBitmap.LoadAsync(ImageCanvas,random);
            if(!SHA256.HashData(reference.GetPixelBytes()).SequenceEqual(SHA256.HashData(adapted.GetPixelBytes())))throw new InvalidDataException("PNG path and stream loading changed pixels.");
            using var native=await LoadLocalBitmap(png);
            if(!SHA256.HashData(reference.GetPixelBytes()).SequenceEqual(SHA256.HashData(native.GetPixelBytes())))throw new InvalidDataException("Native stream loading changed pixels.");
        }
        var samples=new List<object>();
        for(int i=0;i<100;i++)foreach(string mode in i%2==0?new[]{"pngPath","pngStream","pngNativeStream","bgra"}:new[]{"bgra","pngNativeStream","pngStream","pngPath"})
        {
            var timer=Stopwatch.StartNew();
            if(mode=="pngPath"){using var bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,png);samples.Add(new{mode,ms=timer.Elapsed.TotalMilliseconds});}
            else if(mode=="pngNativeStream")
            {
                using var stream=await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(png,Windows.Storage.FileAccessMode.Read);
                using var bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,stream);samples.Add(new{mode,ms=timer.Elapsed.TotalMilliseconds});
            }
            else if(mode=="pngStream")
            {
                using var stream=new FileStream(png,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                using var random=stream.AsRandomAccessStream();using var bitmap=await CanvasBitmap.LoadAsync(ImageCanvas,random);
                samples.Add(new{mode,ms=timer.Elapsed.TotalMilliseconds});
            }
            else{byte[] bytes=await File.ReadAllBytesAsync(raw);using var bitmap=CanvasBitmap.CreateFromBytes(ImageCanvas,bytes,width,height,DirectXPixelFormat.B8G8R8A8UIntNormalized);samples.Add(new{mode,ms=timer.Elapsed.TotalMilliseconds});}
        }
        report["samples"]=samples;report["width"]=width;report["height"]=height;report["equalDisplayedPixels"]=true;
        report["scope"]="Prepared opaque JPEG assets: file read plus bitmap creation only, no decoder/IPC/Present timing";report["status"]="PASS";
    }
}
