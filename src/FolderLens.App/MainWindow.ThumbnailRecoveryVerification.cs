using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyThumbnailRecovery(string source,Dictionary<string,object> report)
    {
        folderGrouping=new(false,"first","logicalBytes","desc","all");sortDescending=false;SelectTag(SortField,"name");
        ChooseBrowserView(ThumbnailMode,new RoutedEventArgs());
        FileRow? failed=null,denied=null,deadline=null,paused=null,exhausted=null,departed=null;
        bool recoverPaused=false;
        var reads=new Dictionary<string,int>();
        var attempts=new List<double>();var watch=Stopwatch.StartNew();
        verifyThumbnailReadBarrier=(row,_)=>
        {
            int count=reads[row.Name]=reads.GetValueOrDefault(row.Name)+1;
            if(row.Name=="image-02.png"){denied=row;throw new UnauthorizedAccessException("Denied fixture.");}
            if(row.Name=="image-03.png"){deadline=row;if(count==1)throw new OperationCanceledException("Inner source deadline.");}
            if(row.Name=="image-04.png"){paused=row;if(!recoverPaused)throw new IOException("IoUnavailable");}
            if(row.Name=="image-05.png"){exhausted=row;throw new IOException("SourceIoError");}
            if(row.Name=="depart.png"){departed=row;throw new IOException("SourceIoError");}
            if(row.Name!="image-00.png")return Task.CompletedTask;
            failed=row;attempts.Add(watch.Elapsed.TotalSeconds);
            if(attempts.Count==1)throw new TimeoutException("Delayed initial source connection.");
            if(attempts.Count==2)throw new IOException("SourceIoError");
            return Task.CompletedTask;
        };
        try
        {
            await OpenRoot(source);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
            monitor?.Dispose();monitor=null;
            await WaitUntil(()=>failed is not null&&visible.Any(row=>row.Name=="image-01.png"&&row.Thumbnail is not null),TimeSpan.FromSeconds(10));
            var good=visible.Single(row=>row.Name=="image-01.png");var bitmap=good.Thumbnail;
            var items=ActiveBrowser.ItemsSource;long navigation=directoryNavigationRequest;
            try{await WaitUntil(()=>failed!.Thumbnail is not null,TimeSpan.FromSeconds(14));}
            finally{report["sourceRecovery"]=new{attempts=attempts.ToArray(),recovered=failed!.Thumbnail is not null,failed.ThumbnailError};}
            if(attempts.Count!=3||attempts.Zip(attempts.Skip(1),(a,b)=>b-a).Any(gap=>gap<4.5))throw new InvalidOperationException("Transient thumbnail retries were missing, duplicated or not spaced.");
            if(failed!.ThumbnailError.Length>0||!ReferenceEquals(good.Thumbnail,bitmap)||!ReferenceEquals(ActiveBrowser.ItemsSource,items)||navigation!=directoryNavigationRequest)
                throw new InvalidOperationException("Thumbnail recovery refreshed healthy content or navigated the browser.");
            report["healthyThumbnailAndBrowserRetained"]=true;
            await WaitUntil(()=>deadline?.Thumbnail is not null&&paused?.ThumbnailRetryPending==true&&exhausted?.ThumbnailRetryPending==true,TimeSpan.FromSeconds(8));
            if(denied is null||reads["image-02.png"]!=1||denied.ThumbnailRetryPending||reads["image-03.png"]!=2)
                throw new InvalidOperationException("Permission failure retried or an inner deadline did not recover.");
            report["innerDeadlineRecoveredAndDenialStayedTerminal"]=true;

            // A view with no visible thumbnail consumer must not poll that source.
            ChooseBrowserView(DetailsMode,new RoutedEventArgs());Shell.UpdateLayout();
            await WaitUntil(()=>thumbnailWorkCount==0&&thumbnailRetryTimer?.IsRunning!=true,TimeSpan.FromSeconds(5));
            int pausedReads=reads["image-04.png"];
            TimeSpan advance=TimeSpan.FromSeconds(30);
            verifyThumbnailRetryTime=()=>TimeSpan.FromMilliseconds(Environment.TickCount64)+advance;
            QueueThumbnailViewport();await Task.Delay(1200);
            if(reads["image-04.png"]!=pausedReads||thumbnailRetryTimer?.IsRunning==true)
                throw new InvalidOperationException("Hidden thumbnails kept retrying.");
            recoverPaused=true;ChooseBrowserView(ThumbnailMode,new RoutedEventArgs());Shell.UpdateLayout();QueueThumbnailViewport();
            await WaitUntil(()=>paused!.Thumbnail is not null,TimeSpan.FromSeconds(5));
            report["hiddenThumbnailPausedAndResumed"]=true;

            await WaitUntil(()=>thumbnailWorkCount==0,TimeSpan.FromSeconds(5));
            int exhaustedReads=reads["image-05.png"];
            advance=TimeSpan.FromMinutes(4);QueueThumbnailViewport();
            await WaitUntil(()=>!exhausted!.ThumbnailRetryPending&&thumbnailRetryTimer?.IsRunning!=true,TimeSpan.FromSeconds(3));
            if(reads["image-05.png"]!=exhaustedReads||!exhausted!.ThumbnailError.Contains("自动重试已停止"))
                throw new InvalidOperationException("Expired recovery continued reading or did not explain its terminal state.");
            report["expiredBudgetStopsTimerAndReads"]=true;

            string next=Path.Combine(source,"B");
            await File.WriteAllBytesAsync(Path.Combine(next,"depart.png"),await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png")));
            await OpenRoot(next);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
            monitor?.Dispose();monitor=null;
            await WaitUntil(()=>departed?.ThumbnailRetryPending==true,TimeSpan.FromSeconds(5));
            string empty=Path.Combine(dataDirectory,"empty-recovery");Directory.CreateDirectory(empty);
            await OpenRoot(empty);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
            int departedReads=reads["depart.png"];advance+=TimeSpan.FromSeconds(10);QueueThumbnailViewport();await Task.Delay(1200);
            if(reads["depart.png"]!=departedReads||thumbnailRetryTimer?.IsRunning==true)
                throw new InvalidOperationException("Retired root retained thumbnail retries.");
            report["navigationRetiresRecovery"]=true;
            report["attemptCounts"]=reads;
            report["status"]="PASS";
        }
        finally{verifyThumbnailReadBarrier=null;verifyThumbnailRetryTime=null;}
    }
}
