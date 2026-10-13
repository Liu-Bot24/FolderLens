using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFunctionalPixelBoundaries(string source,Dictionary<string,object> report)
    {
        string fixtures=source;source=await CreateFunctionalViewerImages(fixtures);folderGrouping=new(false);sortDescending=false;SelectTag(SortField,"name");verifyColdSwitch=true;
        await OpenRoot(source);await SettleFunctionalCollection();await SelectBrowserOrdinal(results!,0,lifetime.Token);
        await RunViewerAction(ViewerAction.Actual);await RunViewerAction(ViewerAction.LockSizing);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyTileReplyBarrier=async(_,_)=>{verifyTileReplyBarrier=null;entered.TrySetResult();await release.Task;};
        var b=SelectBrowserOrdinal(results!,1,lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));await SelectBrowserOrdinal(results!,2,lifetime.Token);long current=selection;
            release.TrySetResult();await b;
            await WaitUntil(()=>tiles.Count>0&&tileGate.CurrentCount==1&&!tileReloadPending,TimeSpan.FromSeconds(10));
            bool correct=selected?.Name=="photo-2.png"&&selection==current&&tiles.Values.All(bitmap=>bitmap.GetPixelBytes()[0]==180);
            report["lateBDiscarded"]=correct;if(!correct)throw new InvalidOperationException("A late B tile replaced C.");
        }
        finally{release.TrySetResult();verifyTileReplyBarrier=null;await b;}
        await RunViewerAction(ViewerAction.LockSizing);int requested=0;verifyFullTileRequested=(_,_)=>requested++;
        await SelectBrowserOrdinal(results!,0,lifetime.Token);await Task.Delay(250);
        report["unlockedDefaultWithoutTiles"]=requested==0&&viewerScaleIntent==ViewerScaleIntent.Default;
        if(!(bool)report["unlockedDefaultWithoutTiles"])throw new InvalidOperationException("Unlocked navigation retained detail mode.");
        source=await CreateFunctionalViewerImages(Path.Combine(fixtures,"small"),64,48,2);
        await OpenRoot(source);await SettleFunctionalCollection();await SelectBrowserOrdinal(results!,0,lifetime.Token);
        await ZoomViewerAt(2/Shell.XamlRoot.RasterizationScale,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));await RunViewerAction(ViewerAction.LockSizing);requested=0;
        await SelectBrowserOrdinal(results!,1,lifetime.Token);await Task.Delay(250);
        bool small=requested==0&&FitContainsOriginalPixels()&&Math.Abs(EffectiveScale()*Shell.XamlRoot.RasterizationScale-2)<.001;report["smallUsesCompleteFit"]=small;
        if(!small)throw new InvalidOperationException("Small locked image repeated original-pixel work.");
        string animated=Path.Combine(fixtures,"animation");Directory.CreateDirectory(animated);
        using(var stream=new InMemoryRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId,stream);
            for(int frame=0;frame<2;frame++)
            {
                byte[] bytes=new byte[64*48*4];for(int i=0;i<bytes.Length;i+=4){bytes[i+frame]=200;bytes[i+3]=255;}
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,64,48,96,96,bytes);
                await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string,BitmapTypedValue>{{"/grctlext/Delay",new((ushort)300,PropertyType.UInt16)}});
                if(frame==0)await encoder.GoToNextFrameAsync();
            }
            await encoder.FlushAsync();stream.Seek(0);using var output=File.Create(Path.Combine(animated,"animated.gif"));await stream.AsStreamForRead().CopyToAsync(output);
        }
        await OpenRoot(animated);await SettleFunctionalCollection();requested=0;await SelectBrowserOrdinal(results!,0,lifetime.Token);
        int first=animationFrameIndex;PreviewColumn.Width=new(PreviewColumn.Width.Value+20);Shell.UpdateLayout();
        await Task.Delay(400);bool initiallyPlaying=animationRunning&&animationFrameIndex==first;
        await WaitUntil(()=>animationFrameIndex!=first,TimeSpan.FromSeconds(4));
        bool playing=initiallyPlaying&&(animationRunning||animationCompleted)&&requested==0&&AnimationButton.Visibility==Visibility.Visible;
        report["animationAdvancesWithoutDetailPause"]=playing;report["animationState"]=new{initiallyPlaying,animationRunning,animationCompleted,requested};
        if(!playing)throw new InvalidOperationException("Locked animation stopped or requested static tiles.");
        verifyFullTileRequested=null;verifyColdSwitch=false;report["status"]="PASS";
    }
    private async Task VerifyFunctionalReading(string source,Dictionary<string,object> report)
    {
        source=await CreateFunctionalViewerImages(source,2000,6000,1);await OpenRoot(source);await SettleFunctionalCollection();await SelectBrowserOrdinal(results!,0,lifetime.Token);
        var checks=new List<object>();report["checks"]=checks;
        double raster=Shell.XamlRoot.RasterizationScale;AppWindow.Resize(new((int)(1280*raster),(int)(1000*raster)));SetPreviewSplit(.2);
        await WaitUntil(()=>Shell.ActualHeight>900,TimeSpan.FromSeconds(3));
        foreach(bool height in new[]{false,true})
        {
            await SetImmersive(false);SetPreviewSplit(.2);Shell.UpdateLayout();while(rotation!=(height?1:0))await RunViewerAction(ViewerAction.Rotate);
            await RunViewerAction(height?ViewerAction.FitHeight:ViewerAction.FitWidth);
            pan=new Vector2(0,(float)(EffectiveScale()*300));ClampPan();
            Vector2 point=-pan/(float)EffectiveScale();if(point.Length()<100)throw new InvalidOperationException("Noncenter reading fixture was clamped to center.");
            async Task Check(string label)
            {
                await Task.Delay(350);Vector2 actual=-pan/(float)EffectiveScale();bool same=Vector2.Distance(actual,point)<1;
                checks.Add(new{height,label,same,expected=point.Y,actual=actual.Y});if(!same)throw new InvalidOperationException("Resize moved the noncenter reading point: "+label);
            }
            PreviewColumn.Width=new(PreviewColumn.Width.Value+40);Shell.UpdateLayout();await Check("side-divider");
            SetPreviewSplit(.3);Shell.UpdateLayout();await Check("horizontal-divider");
            await RunViewerAction(ViewerAction.ToggleWindowViewer);await Check("window-viewer");
            await RunViewerAction(ViewerAction.ReturnBrowser);await Check("return-sidebar");
        }
        foreach(var intent in new[]{ViewerScaleIntent.Fit,ViewerScaleIntent.Default})
        {
            viewerSizing=ViewerSizing.Automatic;viewerScaleIntent=intent;ApplyViewerScaleIntent();pan=Vector2.Zero;
            PreviewColumn.Width=new(PreviewColumn.Width.Value+20);Shell.UpdateLayout();await Task.Delay(350);
            bool okay=viewerScaleIntent==intent&&!ViewerHasOverflow()&&zoom==0;checks.Add(new{intent=intent.ToString(),okay});
            if(!okay)throw new InvalidOperationException("Fit/default regressed on resize.");
        }
        report["status"]="PASS";
    }
}
