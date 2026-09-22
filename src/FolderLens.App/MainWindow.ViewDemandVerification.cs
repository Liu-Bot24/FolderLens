using FolderLens.Core;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyViewDemand(string source,Dictionary<string,object> report)
    {
        var failures=new List<string>();report["failures"]=failures;
        folderGrouping=new FolderGroupingSpec(Enabled:false);UpdateGroupingButton();
        ChooseBrowserView(DetailsMode,new RoutedEventArgs());
        await OpenRoot(source);
        if(scanTask is not null)await scanTask;
        if(metadataTask is not null)await metadataTask;
        await RefreshQuery();
        // Let real initial scan publications settle before observing ownership
        // changes caused solely by the user's view-mode switch.
        var settling=System.Diagnostics.Stopwatch.StartNew();
        var quiet=System.Diagnostics.Stopwatch.StartNew();long request=queryRequest;
        while(quiet.Elapsed<TimeSpan.FromMilliseconds(1500))
        {
            if(settling.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("视图需求验证的初始查询未稳定。");
            await Task.Delay(50);
            if(request!=queryRequest||queryBusy||reconcilePending||scanTask is {IsCompleted:false}){request=queryRequest;quiet.Restart();}
        }
        ChooseBrowserView(DetailsMode,new RoutedEventArgs());Shell.UpdateLayout();
        await WaitUntil(()=>ActiveBrowser==FilesList&&FilesList.Items.Count==12&&thumbnailWorkCount==0&&propertyRequests.Count==0,TimeSpan.FromSeconds(10));

        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRows=new HashSet<FileRow>();int detailsPropertyReads=0;
        verifyThumbnailReadBarrier=async(row,token)=>
        {
            if(!visibleContainers.Any(pair=>ReferenceEquals(pair.Key.View,FilesGrid)&&ReferenceEquals(pair.Value,row)))return;
            heldRows.Add(row);entered.TrySetResult();await release.Task.WaitAsync(token);
        };
        verifyVideoPropertiesRead=_=>{if(ActiveBrowser==FilesList)detailsPropertyReads++;return Task.CompletedTask;};
        try
        {
            ChooseBrowserView(ThumbnailMode,new RoutedEventArgs());Shell.UpdateLayout();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Shell.UpdateLayout();
            var oldTokens=thumbnailRequests.Values.Select(owner=>owner.Token).ToArray();
            int visibleCards=visibleContainers.Count(pair=>ReferenceEquals(pair.Key.View,FilesGrid));
            if(oldTokens.Length==0||visibleCards==0)throw new InvalidOperationException("未阻塞真实可见图卡的缩略图请求。");

            ChooseBrowserView(DetailsMode,new RoutedEventArgs());Shell.UpdateLayout();
            var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(3);
            while(DateTime.UtcNow<deadline&&(!oldTokens.All(token=>token.IsCancellationRequested)||thumbnailWorkCount>0||detailsPropertyReads==0))
            {await Task.Delay(25);Shell.UpdateLayout();}
            bool allCancelled=oldTokens.All(token=>token.IsCancellationRequested);
            bool workExited=thumbnailWorkCount==0;
            bool detached=FilesGrid.ItemsSource is null&&FilesGrid.Visibility==Visibility.Collapsed;
            bool detailsUsable=ActiveBrowser==FilesList&&FilesList.Items.Count==12&&detailsPropertyReads>0;
            report["detailsTransition"]=new
            {
                visibleCards,blockedCards=heldRows.Count,oldRequests=oldTokens.Length,allCancelled,workExited,detached,detailsUsable,
                detailsPropertyReads,remainingThumbnailWork=thumbnailWorkCount,
                heldRowConsumers=heldRows.Select(row=>new{row.Name,consumers=visibleConsumerCounts.GetValueOrDefault(row),
                    grid=visibleContainers.Count(pair=>ReferenceEquals(pair.Key.View,FilesGrid)&&ReferenceEquals(pair.Value,row)),
                    details=visibleContainers.Count(pair=>ReferenceEquals(pair.Key.View,FilesList)&&ReferenceEquals(pair.Value,row))}).ToArray()
            };
            if(!allCancelled)failures.Add("切换到详情后，隐藏图卡的封面请求没有取消。");
            if(!workExited)failures.Add("切换到详情后，旧封面任务没有退出。");
            if(!detached)failures.Add("详情模式仍保留隐藏缩略图控件的数据源。");
            if(!detailsUsable)failures.Add("详情模式的属性读取被旧封面任务阻塞或错误取消。");
        }
        finally
        {
            verifyThumbnailReadBarrier=null;release.TrySetResult();
            await WaitUntil(()=>thumbnailWorkCount==0&&propertyRequests.Count==0,TimeSpan.FromSeconds(10));
            verifyVideoPropertiesRead=null;
        }

        // Cancellation of a hidden view must not disable visible thumbnail work
        // when the user switches back to the same, still-owned result source.
        ChooseBrowserView(ThumbnailMode,new RoutedEventArgs());Shell.UpdateLayout();
        await WaitUntil(()=>visibleContainers.Any(pair=>ReferenceEquals(pair.Key.View,FilesGrid)&&pair.Value.Thumbnail is not null),TimeSpan.FromSeconds(10));
        int recovered=visibleContainers.Where(pair=>ReferenceEquals(pair.Key.View,FilesGrid)).Select(pair=>pair.Value).Distinct().Count(row=>row.Thumbnail is not null);
        bool restored=ActiveBrowser==FilesGrid&&FilesList.ItemsSource is null&&FilesGrid.Items.Count==12&&recovered>0;
        report["thumbnailRecovery"]=new{restored,decodedVisibleCards=recovered,detailsPropertyReads};
        if(!restored)failures.Add("切回缩略图后无法恢复可见图卡加载。");
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        report["status"]="PASS";
    }
}
