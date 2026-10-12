namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyPromotionViewport(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await ResetFirstPageFixture();ResetBrowserStart();
        double before=0;
        verifyFirstPageBarrier=async _=>
        {
            Shell.UpdateLayout();RestoreBrowserStart(FilesGrid);await Task.Delay(50);
            before=CapturePublicationViewport(FilesGrid);
            if(Math.Abs(before)>1)throw new InvalidOperationException("新目录首批没有保持数值偏移0。");
            // Selection alone must not release the top position.
            AcceptBrowserInteraction();
            if(!keepBrowserStart)throw new InvalidOperationException("点击图片错误地放弃了顶部位置。");
        };
        try
        {
            await RefreshQuery(preserveViewport:true,scanPreview:true);await Task.Delay(100);
            double after=FindScrollViewer(FilesGrid)?.VerticalOffset??0;
            report["beforeOffset"]=before;report["afterOffset"]=after;
            if(Math.Abs(after-before)>1)throw new InvalidOperationException("首批升级改变了数值滚动偏移。");
            report["status"]="PASS";
        }
        finally{verifyFirstPageBarrier=null;}
    }
}
