using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyLongImageWheel(string source,Dictionary<string,object> report)
    {
        string tall=Path.Combine(source,"A","image-00.png");
        using(var stream=new InMemoryRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);byte[] pixels=new byte[64*4800*4];
            for(int i=0;i<pixels.Length;i+=4){pixels[i]=(byte)(i/256%255);pixels[i+1]=120;pixels[i+2]=200;pixels[i+3]=255;}
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,4800,96,96,pixels);await encoder.FlushAsync();stream.Seek(0);
            byte[] bytes=new byte[checked((int)stream.Size)];await stream.ReadAsync(bytes.AsBuffer(),(uint)bytes.Length,InputStreamOptions.None);await File.WriteAllBytesAsync(tall,bytes);
        }
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);Shell.UpdateLayout();await RunViewerAction(ViewerAction.FitWidth);
        var before=pan;double scale=EffectiveScale();var row=selected;var position=new Windows.Foundation.Point(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2);
        await ApplyViewerWheel(-120,position,VirtualKeyModifiers.Menu);
        if(selected!=row||EffectiveScale()!=scale||pan.Y>=before.Y||pan.X!=before.X)throw new InvalidOperationException("Alt wheel failed to pan the long image vertically without switching or zooming.");
        report["altWheelPansSameImage"]=true;
        await ApplyViewerWheel(120,position,VirtualKeyModifiers.Control);
        if(selected!=row||EffectiveScale()<=scale)throw new InvalidOperationException("Ctrl wheel no longer zooms.");
        report["controlWheelStillZooms"]=true;
        string prior=wheelBehavior;try{wheelBehavior="next";viewerScaleIntent=ViewerScaleIntent.Default;await ApplyViewerWheel(-120,position,VirtualKeyModifiers.None);}
        finally{wheelBehavior=prior;}
        await WaitUntil(()=>selected?.Ordinal==1&&previewReadySelection==selection,TimeSpan.FromSeconds(10));
        report["plainWheelStillChangesFile"]=true;report["status"]="PASS";
    }
}
