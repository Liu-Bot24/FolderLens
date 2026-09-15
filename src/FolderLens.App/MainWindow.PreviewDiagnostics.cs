using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private long previewDiagnosticSelection=-1,previewStartedAt,previewStageStartedAt;
    private string previewStage="idle";
    private long previewDrawnSelection=-1;
    private void BeginPreviewDiagnostics(long current)
    {
        previewDiagnosticSelection=current;previewStartedAt=previewStageStartedAt=Stopwatch.GetTimestamp();
        scanLog?.Write("preview-request",new{request=current,path=selected is null?null:SourcePath(selected),kind=selected?.Kind,
            version=selected?.Item?.Version,root,fullScreen,immersive});
        RecordPreviewStage(current,"selected");
    }
    private void RecordPreviewStage(long current,string stage)
    {
        if(current!=previewDiagnosticSelection)return;
        previewStage=stage;previewStageStartedAt=Stopwatch.GetTimestamp();
        scanLog?.Write("preview-stage",new{request=current,stage,elapsedMs=Stopwatch.GetElapsedTime(previewStartedAt).TotalMilliseconds});
    }
    private object PreviewDiagnosticState()=>new
    {
        request=previewDiagnosticSelection,stage=previewStage,loading=previewLoading,fullScreen,immersive,
        secondsInStage=previewStageStartedAt==0?0:Stopwatch.GetElapsedTime(previewStageStartedAt).TotalSeconds,
        hasBitmap=fitBitmap is not null,drawnRequest=previewDrawnSelection,sourceWidth,sourceHeight,
        canvasVisible=ImageCanvas.Visibility==Visibility.Visible,canvasWidth=ImageCanvas.ActualWidth,canvasHeight=ImageCanvas.ActualHeight
    };
    private void RecordPreviewDraw()
    {
        if(previewDrawnSelection==selection||verifyBitmapSelection!=selection)return;
        previewDrawnSelection=selection;
        scanLog?.Write("preview-drawn",new{request=selection,elapsedMs=Stopwatch.GetElapsedTime(previewStartedAt).TotalMilliseconds});
    }
    private void RecordPreviewFailure(Exception error)
    {
        scanLog?.Write("preview-error",new{request=selection,path=selected is null?null:SourcePath(selected),stage=previewStage,type=error.GetType().FullName,error.HResult,
            message=error.Message[..Math.Min(error.Message.Length,1024)],worker=error.Data["WorkerDiagnostic"],
            // Local-only debugging details requested by the user; never included in review uploads.
            frames=new StackTrace(error,false).GetFrames().Take(12).Select(frame=>frame.GetMethod()).Select(method=>method?.DeclaringType?.FullName+"."+method?.Name).ToArray()});
        RecordPreviewStage(selection,"failed");
    }
}
