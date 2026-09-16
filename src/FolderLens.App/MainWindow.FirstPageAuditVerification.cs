using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task ResetFirstPageFixture()
    {
        monitor?.Dispose();monitor=null;reconcilePending=false;
        if(scanTask is {} scan)await scan;
        if(queryBusy)await queryCompletion;
        ClearResultSelection();CancelThumbnails();AttachBrowserView(null);results?.Dispose();results=null;
        if(resultHandle is not null)await catalog!.ReleaseSnapshot(resultHandle.Id);resultHandle=null;
        firstPageFilter=null;firstPageSequence=[];firstPageRows.Clear();
    }
    private async Task VerifyFirstPageAudit(string source,Dictionary<string,object> report)
    {
        string scenario=Environment.GetCommandLineArgs().First(arg=>arg.StartsWith("--verify-first-audit-"))["--verify-first-audit-".Length..];
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileRow? row=null,later=null;
        try
        {
            if(scenario=="slideshow")
            {
                await OpenRoot(source);if(metadataTask is not null)await metadataTask;
                await ResetFirstPageFixture();
                var filter=CurrentFilter();var first=await catalog!.ReadFirstPage(filter);
                PublishFirstPage(filter,first.Items);
                var start=firstPageSequence.First(item=>item.Kind=="image");await SelectPreview(start);
                var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var interval=slideTimer!.Interval;verifySlideTickCompleted=()=>finished.TrySetResult();
                try
                {
                    slideShow=true;slideTimer.Interval=TimeSpan.FromMilliseconds(1);slideTimer.Start();
                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if(results is not null||selected is null||selected.Ordinal<=start.Ordinal||selected.Kind!="image")
                        throw new InvalidOperationException("首批列表幻灯片未向后选择图片。");
                }
                finally{slideShow=false;slideTimer.Stop();slideTimer.Interval=interval;verifySlideTickCompleted=null;}
                report["slideshowAdvancesBeforeFullSnapshot"]=true;
            }
            else if(scenario=="selected")
            {
                await OpenRoot(source);if(metadataTask is not null)await metadataTask;
                foreach(bool details in new[]{false,true})
                {
                    DetailsMode.IsChecked=details;await RefreshQuery();
                    await SelectBrowserOrdinal(results!,1,lifetime.Token);
                    var retained=selected??throw new InvalidOperationException("未选中文件。");
                    long before=resultHandle!.Count;string? viewport=VisibleBrowserPath();
                    byte[] image=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
                    await File.WriteAllBytesAsync(Path.Combine(source,$"new-audit-{details}.png"),image);
                    await new FolderLens.Infrastructure.DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);
                    await RefreshQuery(preserveViewport:true,scanPreview:true);
                    if(resultHandle.Count!=before+1||!ReferenceEquals(selected,retained))throw new InvalidOperationException("选中文件后新增扫描结果未发布，或选中对象丢失。");
                    if(viewport is not null&&VisibleBrowserPath()!=viewport)throw new InvalidOperationException("自动更新使视口跳动。");
                    var displayed=resultHandle;slideShow=true;
                    try{await RefreshQuery(scanPreview:true);if(!ReferenceEquals(resultHandle,displayed))throw new InvalidOperationException("幻灯片序列被后台刷新替换。");}
                    finally{slideShow=false;}
                }
                report["selectedGridAndDetailsKeepReceivingScanResults"]=true;
                report["selectionAndViewportRetained"]=true;report["slideshowSequenceStable"]=true;
            }
            else if(scenario=="completion")
            {
                verifyVideoMetadataBarrier=token=>release.Task.WaitAsync(token);
                verifyScanBarrier=async token=>
                {
                    await ResetFirstPageFixture();
                    verifyFirstPageBarrier=async _=>{row=firstPageSequence[1];await SelectPreview(row);};
                    verifyCandidateBarrier=_=>throw new IOException("SelectedFirstPageCandidateFailure");
                    await RefreshQuery();verifyCandidateBarrier=null;verifyFirstPageBarrier=null;
                    if(selected is null||resultHandle is not null)throw new InvalidOperationException("首批选中后失败的前置条件未形成。");
                };
                await OpenRoot(source);
                if(resultHandle is null||!ReferenceEquals(selected,row))throw new InvalidOperationException("真实扫描完成入口没有在首批选中后失败时自动升级。");
                report["scanCompletionRetriesSelectedFirstPage"]=true;
                // The real metadata completion path must also retry this state.
                verifyScanBarrier=null;release.TrySetResult();if(metadataTask is not null)await metadataTask;
                await ResetFirstPageFixture();verifyCandidateBarrier=_=>throw new IOException("MetadataFirstPageFailure");
                verifyFirstPageBarrier=async _=>{row=firstPageSequence[1];await SelectPreview(row);};
                await RefreshQuery();verifyCandidateBarrier=null;verifyFirstPageBarrier=null;
                await StartMetadataRefresh();
                if(resultHandle is null||!ReferenceEquals(selected,row))throw new InvalidOperationException("元数据完成入口没有恢复首批升级。");
                report["metadataCompletionRetriesSelectedFirstPage"]=true;
            }
            else
            {
                await OpenRoot(source);if(metadataTask is not null)await metadataTask;await ResetFirstPageFixture();
                if(scenario=="decode")
                {
                    verifyThumbnailReadBarrier=async (item,token)=>{row??=item;entered.TrySetResult();await release.Task.WaitAsync(token);};
                    verifyFirstPageBarrier=async _=>await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    verifyCandidateBarrier=_=>throw new IOException("PendingDecodeFirstPageFailure");
                    await RefreshQuery();verifyCandidateBarrier=null;verifyFirstPageBarrier=null;
                    await RefreshQuery();release.TrySetResult();
                    await WaitUntil(()=>thumbnailRequests.Count==0,TimeSpan.FromSeconds(12));
                    if(row?.Thumbnail is null)throw new InvalidOperationException("同条件显式重试使保留行的解码失效且没有接续。");
                    report["pendingDecodeSurvivesExplicitRetry"]=true;
                }
                else
                {
                    verifyFirstPageBarrier=async _=>{row=firstPageSequence[1];await SelectPreview(row);};
                    verifySelectionRestoreBarrier=async ()=>{later=(FileRow)results![2]!;await SelectBrowserOrdinal(results,2,lifetime.Token);};
                    await RefreshQuery();
                    if(!ReferenceEquals(selected,later))throw new InvalidOperationException("升级后的旧选择恢复覆盖了用户后来选择。");
                    report["laterSelectionOwnsPreview"]=true;
                }
            }
        }
        finally
        {
            release.TrySetResult();verifyScanBarrier=null;verifyVideoMetadataBarrier=null;verifyCandidateBarrier=null;verifyFirstPageBarrier=null;
            verifyThumbnailReadBarrier=null;verifySelectionRestoreBarrier=null;if(metadataTask is not null)await metadataTask;
        }
    }
}
