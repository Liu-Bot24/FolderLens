using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Action<long,string>? verifyFullTileRequested;
    private Func<long,CancellationToken,Task>? verifyTileReplyBarrier;
    private async Task<string> CreateFunctionalViewerImages(string source,int width=6000,int height=4000,int count=3,bool jpeg=false)
    {
        string directory=Path.Combine(source,jpeg?"functional-viewer-jpeg":"functional-viewer");Directory.CreateDirectory(directory);
        for(int image=0;image<count;image++)
        {
            byte red=(byte)(40+image*70);
            byte[] pixels=await Task.Run(()=>
            {
                var bytes=new byte[checked(width*height*4)];
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {int offset=(y*width+x)*4;bytes[offset]=(byte)(x+31*(x/1024));bytes[offset+1]=(byte)(y+47*(y/1024));bytes[offset+2]=red;bytes[offset+3]=255;}
                return bytes;
            });
            using var stream=new InMemoryRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(jpeg?BitmapEncoder.JpegEncoderId:BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)width,(uint)height,96,96,pixels);
            await encoder.FlushAsync();stream.Seek(0);
            using var output=File.Create(Path.Combine(directory,$"photo-{image}.{(jpeg?"jpg":"png")}"));await stream.AsStreamForRead().CopyToAsync(output);
        }
        return directory;
    }
    private async Task VerifyFunctionalPixels(string source,Dictionary<string,object> report,bool jpeg=false)
    {
        source=await CreateFunctionalViewerImages(source,jpeg:jpeg);folderGrouping=new(false,"first","logicalBytes","desc","all");sortDescending=false;SelectTag(SortField,"name");
        verifyColdSwitch=true;await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;
        var checks=new List<object>();var requests=new List<(long Selection,string Path)>();
        verifyFullTileRequested=(current,path)=>requests.Add((current,path));
        try
        {
            foreach(bool warm in new[]{false,true})foreach(int percent in new[]{100,200})
            {
                verifyColdSwitch=true;prefetchStop.Cancel();if(prefetchTask is not null)await prefetchTask;ClearPrefetchedImages();
                viewerSizing=ViewerSizing.Automatic;await SelectBrowserOrdinal(results!,0,lifetime.Token);
                await ZoomViewerAt(percent/100d/Shell.XamlRoot.RasterizationScale,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));
                await RunViewerAction(ViewerAction.LockSizing);
                await WaitUntil(()=>tileGate.CurrentCount==1&&!tileReloadPending,TimeSpan.FromSeconds(10));
                if(warm){verifyColdSwitch=false;SchedulePrefetch(selection);if(prefetchTask is not null)await prefetchTask;}
                bool hit=false;verifyPreviewStage=stage=>{if(stage=="prefetchHit")hit=true;};requests.Clear();
                await RunViewerAction(ViewerAction.Next);
                await WaitUntil(()=>selected?.Name==$"photo-1.{(jpeg?"jpg":"png")}"&&!previewLoading&&previewReadySelection==selection,TimeSpan.FromSeconds(15));
                bool detailed=false;
                try{await WaitUntil(()=>tiles.Count>0&&tileGate.CurrentCount==1&&!tileReloadPending,TimeSpan.FromSeconds(4));detailed=true;}
                catch(TimeoutException){}
                int currentRequests=requests.Count(item=>item.Selection==selection&&item.Path==SourcePath(selected!));
                bool pixelsCorrect=detailed&&tiles.All(tile=>
                {
                    var bitmap=tile.Value;
                    var bytes=bitmap.GetPixelBytes();int offset=checked((int)bitmap.SizeInPixels.Width*4*17+13*4);
                    int tolerance=jpeg?5:0;
                    return bytes.Length>offset+3&&Math.Abs(bytes[offset]-110)<=tolerance&&Math.Abs(bytes[offset+1]-(byte)(17+47*tile.Key.Item2))<=tolerance&&Math.Abs(bytes[offset+2]-(byte)(13+31*tile.Key.Item1))<=tolerance&&bytes[offset+3]==255;
                });
                checks.Add(new{warm,percent,hit,physicalScale=EffectiveScale()*Shell.XamlRoot.RasterizationScale,fitWidth=fitBitmap?.SizeInPixels.Width,fitHeight=fitBitmap?.SizeInPixels.Height,sourceWidth,sourceHeight,currentRequests,tiles=tiles.Count,pixelsCorrect});
                report["checks"]=checks;
                if(hit!=warm||!pixelsCorrect||currentRequests==0)throw new InvalidOperationException("Locked large-image navigation did not deliver the current original pixels without another gesture.");
            }
            report["status"]="PASS";
        }
        finally{verifyColdSwitch=false;verifyFullTileRequested=null;verifyPreviewStage=null;}
    }
    private async Task VerifyFunctionalSize(string source,Dictionary<string,object> report)
    {
        source=await CreateFunctionalViewerImages(source,2000,6000,1);verifyColdSwitch=true;
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;await SelectBrowserOrdinal(results!,0,lifetime.Token);
        var previous=PreviewColumn.Width;var originalSize=AppWindow.Size;
        try
        {
            double raster=Shell.XamlRoot.RasterizationScale;
            AppWindow.Resize(new((int)Math.Ceiling(1280*raster),(int)Math.Ceiling(1000*raster)));
            await WaitUntil(()=>Shell.ActualHeight>900&&ImageCanvas.ActualHeight>0,TimeSpan.FromSeconds(3));
            await RunViewerAction(ViewerAction.FitWidth);double before=ImageCanvas.ActualWidth,oldScale=EffectiveScale();
            PreviewColumn.Width=new GridLength(previous.Value+120);Shell.UpdateLayout();
            await WaitUntil(()=>Math.Abs(ImageCanvas.ActualWidth-before)>50,TimeSpan.FromSeconds(3));
            await Task.Delay(500);
            double imageWidth=sourceWidth*EffectiveScale();
            report["widthResize"]=new{before,after=ImageCanvas.ActualWidth,oldScale,scale=EffectiveScale(),imageWidth,intent=viewerScaleIntent.ToString()};
            if(Math.Abs(imageWidth-ImageCanvas.ActualWidth)>1)throw new InvalidOperationException("Fit-width did not follow the same-DPI viewport resize.");
            var checks=new List<object>();report["resizeMatrix"]=checks;
            foreach(bool locked in new[]{false,true})foreach(int angle in new[]{0,1})foreach(bool width in new[]{true,false})
            {
                viewerSizing=ViewerSizing.Automatic;
                while(rotation!=angle)await RunViewerAction(ViewerAction.Rotate);
                await RunViewerAction(width?ViewerAction.FitWidth:ViewerAction.FitHeight);
                if(locked)await RunViewerAction(ViewerAction.LockSizing);
                pan=System.Numerics.Vector2.Zero;
                double targetBefore=width?ImageCanvas.ActualWidth:ImageCanvas.ActualHeight;
                if(width)PreviewColumn.Width=new GridLength(PreviewColumn.Width.Value+(locked?-80:80));
                else{var size=AppWindow.Size;AppWindow.Resize(new(size.Width,size.Height+(locked?-100:100)));}
                Shell.UpdateLayout();
                await WaitUntil(()=>Math.Abs((width?ImageCanvas.ActualWidth:ImageCanvas.ActualHeight)-targetBefore)>20,TimeSpan.FromSeconds(3));
                double extent=width?(angle==0?sourceWidth:sourceHeight):(angle==0?sourceHeight:sourceWidth);
                await WaitUntil(()=>Math.Abs(extent*EffectiveScale()-(width?ImageCanvas.ActualWidth:ImageCanvas.ActualHeight))<=1,TimeSpan.FromSeconds(3));
                double target=width?ImageCanvas.ActualWidth:ImageCanvas.ActualHeight;
                checks.Add(new{locked,angle,width,target,rendered=extent*EffectiveScale(),centerPreserved=pan.Length()<1});
                if(pan.Length()>=1)throw new InvalidOperationException("Resize changed the centered reading position.");
            }
            viewerSizing=ViewerSizing.Automatic;
            foreach(int percent in new[]{100,200})
            {
                await ZoomViewerAt(percent/100d/Shell.XamlRoot.RasterizationScale,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));
                PreviewColumn.Width=new GridLength(PreviewColumn.Width.Value+30);Shell.UpdateLayout();await Task.Delay(350);
                double physical=EffectiveScale()*Shell.XamlRoot.RasterizationScale;
                if(Math.Abs(physical-percent/100d)>.0001)throw new InvalidOperationException("Resize changed a custom physical scale.");
                checks.Add(new{custom=percent,physical});
            }
            report["status"]="PASS";
        }
        finally{PreviewColumn.Width=previous;AppWindow.Resize(originalSize);verifyColdSwitch=false;}
    }
}
