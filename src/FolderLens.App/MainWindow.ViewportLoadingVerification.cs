using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyViewportLoading(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=12;i<96;i++)await File.WriteAllBytesAsync(Path.Combine(source,"A",$"image-{i:D2}.png"),sample);
        await OpenRoot(source);await ResetFirstPageFixture();
        await VerifyRetiredViewportRead();
        folderGrouping=new(true,"first","logicalBytes","desc","all");ResetBrowserStart();
        FileRow? chosen=null;
        double chosenTop=0;
        verifyFirstPageBarrier=async token=>
        {
            if(resultHandle is not null||firstPageSequence.Length==0||browserGroups is not null)
                throw new InvalidOperationException("分组完成前没有提供可用首批。");
            Shell.UpdateLayout();RestoreBrowserStart(FilesGrid);
            if(!keepBrowserStart||Math.Abs(CapturePublicationViewport(FilesGrid))>1||(FindScrollViewer(FilesGrid)?.VerticalOffset??0)>1)
                throw new InvalidOperationException("未操作的新目录没有保持开头。");
            chosen=firstPageSequence.First(row=>row.Kind=="image");
            FilesGrid.SelectedItem=chosen;
            await WaitUntil(()=>ReferenceEquals(selected,chosen)&&fitBitmap is not null,TimeSpan.FromSeconds(8));
            if(FilesGrid.ContainerFromItem(chosen) is not Microsoft.UI.Xaml.FrameworkElement element)throw new InvalidOperationException("首批选择没有可见容器。");
            chosenTop=element.TransformToVisual(FilesGrid).TransformPoint(new(0,0)).Y;
            if(fitBitmap is null||!ReferenceEquals(selected,chosen))throw new InvalidOperationException("分组首批不能直接预览。");
        };
        try
        {
            // This is an explicit grouping choice. Automatic scan publication
            // intentionally ignores unsubmitted control changes.
            await RefreshQuery(preserveViewport:true);
            report["promotionState"]=new{grouping=submittedBrowserFilter?.Grouping.Enabled,groups=browserGroups?.Count,chosen=chosen is not null,
                selectionRetained=chosen is not null&&ReferenceEquals(selected,chosen),rowRetained=chosen is not null&&results?.Contains(chosen)==true,hasBitmap=fitBitmap is not null};
            if(browserGroups is null||chosen is null||!ReferenceEquals(selected,chosen)||results?.Contains(chosen)!=true||fitBitmap is null)
                throw new InvalidOperationException("正式分组发布丢失了首批选择或预览。");
            await WaitUntil(()=>FilesGrid.ContainerFromItem(chosen) is Microsoft.UI.Xaml.FrameworkElement element
                &&Math.Abs(element.TransformToVisual(FilesGrid).TransformPoint(new(0,0)).Y-chosenTop)<=1
                &&IsThumbnailInViewport(FilesGrid,chosen),TimeSpan.FromSeconds(4));
            report["firstPageSelectionStaysAtVisiblePosition"]=true;
            report["groupedFirstPagePreviewSurvivesPromotion"]=true;
            var saved=CaptureView() with{ScrollOffset=99999,ScrollAnchorPath="not-scanned-yet.png"};
            pendingViewRestore=new(saved,viewRestoreRevision,rootChangeVersion,browserInteractionVersion,directoryNavigationRequest);
            await SelectPreview(chosen);
            if(pendingViewRestore is not null)throw new InvalidOperationException("新选择没有撤销旧历史恢复。");
            pendingViewRestore=new(saved,viewRestoreRevision,rootChangeVersion,browserInteractionVersion,directoryNavigationRequest);
            Navigate(0);
            if(pendingViewRestore is not null)throw new InvalidOperationException("查看器导航没有撤销旧历史恢复。");
            report["newSelectionCancelsHistoryRestore"]=true;
            report["retiredPageReadMigratesAndUserCancellationStops"]=true;
            report["status"]="PASS";
        }
        finally{verifyFirstPageBarrier=null;pendingViewRestore=null;}
    }
    private async Task VerifyRetiredViewportRead()
    {
        var prior=results;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var old=new VirtualResults(1,async(_,token)=>
        {
            entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);
            throw new InvalidOperationException("Retired read resumed unexpectedly.");
        });
        var item=new SnapshotItem(0,"retained-file",1,"retained.png","directory",1,null,"image");
        using var next=new VirtualResults(1,(_,_)=>Task.FromResult<IReadOnlyList<SnapshotItem>>([item]));
        try
        {
            results=old;var row=(FileRow)old[0]!;
            var read=EnsureCurrentRowLoaded(row,lifetime.Token,foreground:true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            next.Retain(row,0,null);results=next;old.Dispose();
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            if(row.Item?.EntryId!=item.EntryId)throw new InvalidOperationException("旧源退役后未接续当前行。");

            var cancelledEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var blocked=new VirtualResults(1,async(_,token)=>
            {
                cancelledEntered.TrySetResult();await Task.Delay(Timeout.Infinite,token);
                throw new InvalidOperationException("Cancelled read resumed unexpectedly.");
            });
            using var cancelled=new CancellationTokenSource();results=blocked;
            var target=(FileRow)blocked[0]!;var stopped=EnsureCurrentRowLoaded(target,cancelled.Token,foreground:true);
            await cancelledEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));cancelled.Cancel();
            next.Retain(target,0,null);results=next;blocked.Dispose();
            try{await stopped.WaitAsync(TimeSpan.FromSeconds(3));throw new InvalidOperationException("用户取消后仍继续读取。");}
            catch(OperationCanceledException) when(cancelled.IsCancellationRequested){}
            if(target.Item is not null)throw new InvalidOperationException("已取消行被填充。");
        }
        finally{results=prior;}
    }
}
