using System.Diagnostics;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFirstPage(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);
        if(metadataTask is not null)await metadataTask;
        await RefreshQuery();
        CancelThumbnails();AttachBrowserView(null);results?.Dispose();results=null;
        if(resultHandle is not null)await catalog!.ReleaseSnapshot(resultHandle.Id);
        resultHandle=null;
        bool observed=false;string? failure=null;
        verifyFirstPageBarrier=async token=>
        {
            if(FilesGrid.Items.Count==0)return;
            observed=true;
            try
            {
            var rows=FilesGrid.Items.Cast<FileRow>().ToArray();
            Shell.UpdateLayout();
            var timer=Stopwatch.StartNew();
            while(rows.All(row=>row.Thumbnail is null)&&timer.Elapsed<TimeSpan.FromSeconds(8))
                await Task.Delay(25,token);
            report["firstThumbnailMs"]=timer.Elapsed.TotalMilliseconds;
            report["beforeSnapshot"]=resultHandle is null;
            if(resultHandle is not null||rows.All(row=>row.Thumbnail is null))
                failure="首批卡片在完整快照建立前没有加载任何缩略图";
            }
            catch(Exception ex){failure=ex.ToString();}
        };
        try{await RefreshQuery();}finally{verifyFirstPageBarrier=null;}
        if(!observed)throw new InvalidOperationException("未经过首批列表发布路径");
        if(!report.ContainsKey("beforeSnapshot"))throw new InvalidOperationException("首批检查未完成："+failure);
        if(failure is not null)throw new InvalidOperationException(failure);
        await WaitUntil(()=>visible.Any(row=>row.Thumbnail is not null),TimeSpan.FromSeconds(8));
        report["afterSnapshotThumbnails"]=true;
        report["status"]="PASS";
    }
}
