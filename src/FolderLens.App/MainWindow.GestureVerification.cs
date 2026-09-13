using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyPressGesture(string directory,Dictionary<string,object> report)
    {
        root=directory;selected=new FileRow(0);selected.Fill(new(0,"gesture",1,"A/image-00.png","",0,null,"image"));
        fitBitmap=await CanvasBitmap.LoadAsync(ImageCanvas,Path.Combine(directory,"A","image-00.png"));
        sourceWidth=64;sourceHeight=48;previewLoading=false;immersive=true;viewerPressZoom=new();
        var point=new Point(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2);
        BeginViewerPress(1,point);
        report["immediateState"]=viewerGesture.ToString();
        if(viewerGesture!=ViewerGesture.Pressed)throw new InvalidOperationException("瞬间按下已经进入250%放大，单击与长按未区分。");
        await Task.Delay(60);
        if(viewerGesture!=ViewerGesture.Pressed)throw new InvalidOperationException("正常快速单击期间出现临时放大。");
        await CompleteViewerPress(point);
        if(viewerTapTimer?.IsRunning!=true)throw new InvalidOperationException("快速单击没有产生100%查看动作。");
        ResetViewerGesture();
        BeginViewerPress(2,point);await Task.Delay(260);
        if(viewerGesture!=ViewerGesture.Magnifier||viewerPressZoom.Percent!=250)throw new InvalidOperationException("持续按住没有按默认250%放大。");
        await CompleteViewerPress(point);
        if(viewerTapTimer?.IsRunning==true||viewerGesture!=ViewerGesture.None)throw new InvalidOperationException("松开长按又触发了单击。");
        BeginViewerPress(3,point);ResetViewerGesture();await Task.Delay(260);
        if(viewerGesture!=ViewerGesture.None)throw new InvalidOperationException("取消后仍触发长按。");
        report["tapAndHoldExclusive"]=true;report["status"]="PASS";
    }
}
