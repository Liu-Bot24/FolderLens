using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<string,CancellationToken,Task>? verifyPreviewUpdateBarrier;
    private Func<FileRow,Task>? verifyThumbnailCleanupBarrier;

    private async Task VerifyPreviewErrorOwnership(string source,Dictionary<string,object> report)
    {
        await File.WriteAllTextAsync(Path.Combine(source,"replacement.txt"),"The current preview owns its status.");
        suppressFilters=true;try{SelectTag(Category,"all");}finally{suppressFilters=false;}
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;await RefreshQuery();
        async Task<FileRow> Row(string path)
        {
            long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,path,lifetime.Token)??throw new InvalidOperationException("Missing fixture: "+path);
            var row=(FileRow)results![checked((int)ordinal)]!;await results.EnsureLoaded(row,lifetime.Token);return row;
        }
        var image=await Row("A\\image-00.png");var plain=await Row("replacement.txt");
        var failures=new List<string>();report["errors"]=failures;
        var checks=new List<object>();report["checks"]=checks;
        string cloudKey=CloudKey(plain);bool previouslyApproved=approvedCloud.Contains(cloudKey);approvedCloud.Add(cloudKey);
        try
        {
            foreach(string entry in new[]{"resize","immersive","fullscreen","cloud"})
            {
                Task Invoke()=>entry switch
                {
                    "resize"=>RefreshFitAfterResize(),
                    "immersive"=>SetImmersive(true),
                    // Exercise the actual post-entry refresh without changing the desktop presenter.
                    "fullscreen"=>RefreshFullScreenFit(viewerModeRevision),
                    _=>ReadCloudPreview()
                };
                foreach(bool retired in new[]{true,false})
                {
                    await SetImmersive(false);await SelectPreview(entry=="cloud"?plain:image);
                    zoom=0;animationRunning=false;
                    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    verifyPreviewUpdateBarrier=async(_,_)=>{entered.TrySetResult();await release.Task;throw new IOException("Controlled preview update failure.");};
                    Task pending=Invoke();
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        verifyPreviewUpdateBarrier=null;
                        if(retired)await SelectPreview(entry=="cloud"?image:plain);
                        string expectedQuality=QualityLabel.Text;
                        release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(3));
                        bool passed=retired?previewFailure is null&&QualityLabel.Text==expectedQuality:
                            previewFailure is not null&&QualityLabel.Text==previewFailure;
                        checks.Add(new{entry,retired,passed,quality=QualityLabel.Text});
                        if(!passed)failures.Add(retired?$"{entry}: retired failure overwrote the current preview.":$"{entry}: current failure was not reported.");
                    }
                    finally{release.TrySetResult();verifyPreviewUpdateBarrier=null;await pending;}
                }
            }

            foreach(string entry in new[]{"immersive","fullscreen"})
            {
                await SetImmersive(false);await SelectPreview(image);zoom=0;animationRunning=false;
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                verifyPreviewUpdateBarrier=async(_,_)=>{entered.TrySetResult();await release.Task;throw new IOException("Retired preview layout failure.");};
                Task pending=entry=="immersive"?SetImmersive(true):RefreshFullScreenFit(viewerModeRevision);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));verifyPreviewUpdateBarrier=null;
                    if(entry=="immersive")await SetImmersive(false);else viewerModeRevision++;
                    string expectedQuality=QualityLabel.Text;
                    release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(3));
                    bool passed=previewFailure is null&&QualityLabel.Text==expectedQuality;
                    checks.Add(new{entry=entry+"-layout",retired=true,passed,quality=QualityLabel.Text});
                    if(!passed)failures.Add(entry+": retired layout failure overwrote the current presentation of the same image.");
                }
                finally{release.TrySetResult();verifyPreviewUpdateBarrier=null;await pending;}
            }

            await SetImmersive(false);ChooseBrowserView(DetailsMode,new RoutedEventArgs());Shell.UpdateLayout();
            await WaitUntil(()=>thumbnailWorkCount==0,TimeSpan.FromSeconds(10));
            foreach(bool retired in new[]{true,false})
            {
                CancelThumbnails();var container=new GridViewItem();BindVisibleContainer(FilesGrid,container,plain);
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                verifyThumbnailCleanupBarrier=async row=>{if(!ReferenceEquals(row,plain))return;entered.TrySetResult();await release.Task;throw new IOException("Controlled thumbnail cleanup failure.");};
                Task pending=LoadThumbnail(FilesGrid,plain);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));verifyThumbnailCleanupBarrier=null;
                    if(retired)CancelThumbnails();
                    const string expectedStatus="Current browser operation.";Status.Text=expectedStatus;
                    release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(3));
                    bool passed=retired?Status.Text==expectedStatus&&webviewEvents.Any(value=>value.StartsWith("Retired thumbnail cleanup failed:",StringComparison.Ordinal)):
                        Status.Text.StartsWith("操作未完成：",StringComparison.Ordinal);
                    checks.Add(new{entry="thumbnail-cleanup",retired,passed,quality=Status.Text});
                    if(!passed)failures.Add(retired?"Retired thumbnail cleanup did not preserve the current browser status and diagnostics.":"Current thumbnail cleanup failure was not reported.");
                }
                finally{release.TrySetResult();verifyThumbnailCleanupBarrier=null;await pending;CancelThumbnails();}
            }
        }
        finally{verifyPreviewUpdateBarrier=null;verifyThumbnailCleanupBarrier=null;if(!previouslyApproved)approvedCloud.Remove(cloudKey);await SetImmersive(false);}
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" ",failures));
        report["status"]="PASS";
    }
}
