using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyPathInitialization(string source,Dictionary<string,object> report)
    {
        folderGrouping=new(false);sortDescending=false;SelectTag(SortField,"name");
        string readyProvider=providerIdentity;providerIdentity="";
        browserRuntimeReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        RootPath.Text=Path.Combine(source,"A");
        var opening=NavigateFolderPath(RootPath.Text);
        try
        {
            await Task.Delay(1800);
            var errors=visible.Where(row=>row.ThumbnailError.Length>0).Select(row=>new{row.Name,row.ThumbnailError}).ToArray();
            report["beforeProviderReady"]=new{errors,visible=visible.Count};
            if(errors.Length>0||visible.Count>0||opening.IsCompleted)throw new InvalidOperationException("Address navigation used the thumbnail runtime before its provider identity was ready.");
        }
        finally{providerIdentity=readyProvider;browserRuntimeReady.TrySetResult(true);await opening;}
        if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        await WaitForPathThumbnails();
        if(visible.Any(row=>row.ThumbnailError.Length>0))throw new InvalidOperationException("Initialization left thumbnails permanently unavailable.");
        // Both entry points queue behind the same runtime. The later request owns
        // navigation even when the earlier continuation happens to resume first.
        browserRuntimeReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeOpening=NavigateFolder(new(Path.Combine(source,"B"),"B"));
        var latestOpening=NavigateFolderPath(source);
        try
        {
            await Task.Delay(100);
            if(treeOpening.IsCompleted||latestOpening.IsCompleted)throw new InvalidOperationException("A folder entry bypassed runtime readiness.");
        }
        finally{browserRuntimeReady.TrySetResult(true);await Task.WhenAll(treeOpening,latestOpening);}
        if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        await WaitForPathThumbnails();
        if(BrowsedDirectory!=source)throw new InvalidOperationException("An older queued folder request replaced the latest address entry.");
        report["queuedTreeAndAddress"]=true;
        report["status"]="PASS";
    }
    private async Task WaitForPathThumbnails()=>await WaitUntil(()=>
    {
        var inView=visible.Where(HasThumbnailViewport).ToArray();
        return inView.Length>0&&inView.All(row=>row.Thumbnail is not null&&row.ThumbnailError.Length==0);
    },TimeSpan.FromSeconds(10));
    private async Task VerifyDirectPathThumbnails(string source,Dictionary<string,object> report)
    {
        folderGrouping=new(true,"first","name","asc","all");sortDescending=false;SelectTag(SortField,"name");
        var checks=new List<object>();report["checks"]=checks;
        foreach(string path in new[]{Path.Combine(source,"A").ToLowerInvariant()+"\\",source.ToUpperInvariant(),"\\\\?\\"+Path.Combine(source,"A"),Path.Combine(source,"A"),Path.Combine(source,"A")})
        {
            RootPath.Text=path;await NavigateFolderPath(RootPath.Text);
            if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
            monitor?.Dispose();monitor=null;
            bool loaded=false;
            try{await WaitForPathThumbnails();loaded=true;}
            catch(TimeoutException){}
            checks.Add(new{path=Path.GetRelativePath(source,path),loaded,count=resultHandle?.Count,visible=visible.Count,containers=visibleContainers.Count,
                rows=visible.Select(row=>new{row.Name,state=row.ThumbnailState.ToString(),row.ThumbnailError,hasThumbnail=row.Thumbnail is not null}).ToArray()});
            if(!loaded)throw new InvalidOperationException("Direct path navigation did not load visible thumbnails without navigating away.");
        }
        await NavigateFolder(new(source,"fixture"));
        if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        await WaitForPathThumbnails();
        long treeCount=resultHandle!.Count;
        await NavigateFolderPath(Path.Combine(source,"A"));
        await NavigateFolderPath(source);
        if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        await WaitForPathThumbnails();
        if(resultHandle?.Count!=treeCount)throw new InvalidOperationException("Address and tree navigation produced different folder results.");
        report["treeAddressParity"]=new{treeCount,addressCount=resultHandle.Count};
        report["status"]="PASS";
    }

    private async Task VerifyImageTransition(string source,Dictionary<string,object> report)
    {
        source=await CreateFunctionalViewerImages(source,3000,2200,3);
        folderGrouping=new(false);sortDescending=false;SelectTag(SortField,"name");
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;
        var checks=new List<object>();report["checks"]=checks;
        foreach(var mode in new[]{"cold","warm-detail","cache-eviction"})
        {
        verifyColdSwitch=mode!="warm-detail";
        viewerSizing=ViewerSizing.Automatic;
        await SelectBrowserOrdinal(results!,0,lifetime.Token);
        await WaitUntil(()=>fitBitmap is not null&&!previewLoading&&previewReadySelection==selection,TimeSpan.FromSeconds(10));
        if(mode=="warm-detail")
        {
            await ZoomViewerAt(2/Shell.XamlRoot.RasterizationScale,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));
            await WaitUntil(()=>tiles.Count>0&&tileGate.CurrentCount==1,TimeSpan.FromSeconds(10));
            SchedulePrefetch(selection);if(prefetchTask is not null)await prefetchTask;
        }
        var previous=fitBitmap;double scale=EffectiveScale();var offset=pan;int angle=rotation;
        var oldTiles=tiles.ToArray();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyPreviewBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
        Task? moving=null;
        try
        {
            moving=SelectBrowserOrdinal(results!,1,lifetime.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(mode=="cache-eviction")ClearPrefetchedImages();
            await Task.Delay(120);
            bool retained=ReferenceEquals(previous,fitBitmap)&&fitBitmap is not null&&ImageCanvas.Opacity==1&&ImageCanvas.Visibility==Visibility.Visible
                &&EffectiveScale()==scale&&pan==offset&&rotation==angle&&oldTiles.All(tile=>tiles.TryGetValue(tile.Key,out var bitmap)&&ReferenceEquals(bitmap,tile.Value));
            checks.Add(new{mode,retained,tiles=oldTiles.Length,hasBitmap=fitBitmap is not null,ImageCanvas.Opacity,previewLoading});
            if(!retained)throw new InvalidOperationException("Image navigation cleared or dimmed the previous frame before the replacement was ready.");
        }
        finally{verifyPreviewBarrier=null;release.TrySetResult();if(moving is not null)await moving;}
        await WaitUntil(()=>previewReadySelection==selection&&!previewLoading,TimeSpan.FromSeconds(10));
        if(ReferenceEquals(previous,fitBitmap)||fitBitmap is null)throw new InvalidOperationException("Replacement frame did not arrive.");
        }
        verifyColdSwitch=false;
        var retainedFrame=fitBitmap;
        var firstEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLatest=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests=0;
        verifyPreviewBarrier=async token=>
        {
            if(++requests==1){firstEntered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            else{secondEntered.TrySetResult();await releaseLatest.Task.WaitAsync(token);}
        };
        Task? superseded=null,latest=null;
        try
        {
            superseded=SelectBrowserOrdinal(results!,0,lifetime.Token);await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            latest=SelectBrowserOrdinal(results!,2,lifetime.Token);await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await superseded;
            if(!ReferenceEquals(retainedFrame,fitBitmap)||!previewLoading)throw new InvalidOperationException("Retired navigation cleared the held frame or loading state.");
        }
        finally{verifyPreviewBarrier=null;releaseLatest.TrySetResult();if(latest is not null)await latest;}
        if(selected?.Name!="photo-2.png"||fitBitmap is null||verifyBitmapSelection!=selection)throw new InvalidOperationException("The newest navigation did not own the replacement.");
        report["supersededNavigation"]=true;
        // A current request that becomes invalid must show its error, not leave
        // an older photograph presented under the failed file's name.
        verifyPreviewBarrier=_=>throw new OperationCanceledException("Controlled source invalidation");
        try{await SelectBrowserOrdinal(results!,0,lifetime.Token);}
        finally{verifyPreviewBarrier=null;}
        if(fitBitmap is not null||previewFailure is null)throw new InvalidOperationException("Failed navigation mislabeled the retained frame as the new file.");
        report["currentFailureClearsOldFrame"]=true;
        report["status"]="PASS";
    }
}
