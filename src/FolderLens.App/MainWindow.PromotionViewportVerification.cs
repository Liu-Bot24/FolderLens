namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyPromotionViewport(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        ClearResultSelection();CancelThumbnails();AttachBrowserView(null);results?.Dispose();results=null;
        if(resultHandle is not null)await catalog!.ReleaseSnapshot(resultHandle.Id);resultHandle=null;
        (FileRow Row,double Top)? before=null;
        verifyFirstPageBarrier=async _=>
        {
            Shell.UpdateLayout();FindScrollViewer(FilesGrid)?.ChangeView(null,0,null,true);await Task.Delay(50);
            before=CapturePublicationViewport(FilesGrid);
            if(before is null)throw new InvalidOperationException("No first-page viewport anchor.");
        };
        try
        {
            await RefreshQuery(preserveViewport:true,scanPreview:true);await Task.Delay(100);
            var element=before is {} saved?FilesGrid.ContainerFromItem(saved.Row) as Microsoft.UI.Xaml.FrameworkElement:null;
            if(element is null)throw new InvalidOperationException("First-page anchor disappeared.");
            double after=element.TransformToVisual(FilesGrid).TransformPoint(new(0,0)).Y;
            report["beforeTop"]=before!.Value.Top;report["afterTop"]=after;
            if(Math.Abs(after-before.Value.Top)>1)throw new InvalidOperationException("Promotion scrolled a stationary first-page anchor.");
            report["status"]="PASS";
        }
        finally{verifyFirstPageBarrier=null;}
    }
}
