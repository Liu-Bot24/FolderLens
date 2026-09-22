using FolderLens.Core;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyInvalidFilterSwitch(string source,Dictionary<string,object> report)
    {
        var failures=new List<string>();report["failures"]=failures;
        await File.WriteAllTextAsync(Path.Combine(source,"invalid-filter-index.md"),"# Text category fixture");
        suppressFilters=true;
        try{SelectTag(Category,"text");MinSize.Value=double.NaN;MaxSize.Value=double.NaN;}
        finally{suppressFilters=false;}
        folderGrouping=new FolderGroupingSpec(Enabled:true);UpdateGroupingButton();
        await OpenRoot(source);
        if(scanTask is not null)await scanTask;
        if(metadataTask is not null)await metadataTask;
        await RefreshQuery();
        await WaitUntil(()=>results?.Count==1&&!queryBusy,TimeSpan.FromSeconds(10));
        var initial=await catalog!.ReadPage(resultHandle!.Id,0,10);
        if(initial.Count!=1||initial[0].Kind!="markdown")throw new InvalidOperationException("非法条件切换验证未建立文本基线。");

        // Leave a real replacement query in flight while the valid text list
        // remains displayed. An invalid new request must cancel this query too,
        // otherwise its later completion can restore the wrong category.
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken previousToken=default;
        verifyCandidateBarrier=async token=>{previousToken=token;entered.TrySetResult();await release.Task.WaitAsync(token);};
        Task previousQuery=RefreshQuery();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(results?.Count!=1||ActiveBrowser.Items.Count!=1)throw new InvalidOperationException("同条件刷新未保留文本基线。");
            suppressFilters=true;
            try{MinSize.Value=100;MaxSize.Value=1;}
            finally{suppressFilters=false;}
            SelectTag(Category,"media");
            var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(2);
            while(DateTime.UtcNow<deadline&&(!previousToken.IsCancellationRequested||ActiveBrowser.Items.Count>0||browserEmptyError is null))
                await Task.Delay(20);
            Shell.UpdateLayout();
            bool cancelled=previousToken.IsCancellationRequested;
            bool staleRows=ActiveBrowser.Items.Count>0||resultHandle is not null||results is not null||firstPageSequence.Length>0;
            bool errorVisible=browserEmptyError is not null&&BrowserEmptyState.Visibility==Visibility.Visible
                &&!string.IsNullOrWhiteSpace(BrowserEmptyDescription.Text);
            report["invalidSwitch"]=new{category=Tag(Category),previousQueryCancelled=cancelled,staleRowsVisible=staleRows,errorVisible};
            if(Tag(Category)!="media")failures.Add("无效数值条件下分类没有切换到图片＋视频。");
            if(!cancelled)failures.Add("无效数值条件下切换分类没有取消旧文本查询。");
            if(staleRows)failures.Add("无效数值条件下图片＋视频仍保留旧文本结果。");
            if(!errorVisible)failures.Add("无效数值条件没有显示明确的列表错误状态。");
        }
        finally
        {
            release.TrySetResult();verifyCandidateBarrier=null;
            await previousQuery.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await queryCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        bool lateStaleRows=resultHandle is not null||results is not null||ActiveBrowser.Items.Count>0;
        report["staleRowsAfterPreviousQueryCompleted"]=lateStaleRows;
        if(lateStaleRows)failures.Add("旧查询完成后仍可恢复无效分类下的旧列表。");

        // Correcting the numerical input must recover the already selected
        // media category, without requiring another category toggle or restart.
        suppressFilters=true;
        try{MinSize.Value=double.NaN;MaxSize.Value=double.NaN;}
        finally{suppressFilters=false;}
        await RefreshQuery();
        await WaitUntil(()=>!queryBusy,TimeSpan.FromSeconds(10));
        var recovered=resultHandle is null?[]:await catalog.ReadPage(resultHandle.Id,0,100);
        bool recoveredMedia=Tag(Category)=="media"&&lastAppliedFilter?.Kinds.SequenceEqual(new[]{"image","video"})==true
            &&resultHandle?.Count==12&&recovered.Count==12&&recovered.All(item=>item.Kind is "image" or "video")&&browserEmptyError is null;
        report["recovery"]=new{mediaRecovered=recoveredMedia,count=resultHandle?.Count};
        if(!recoveredMedia)failures.Add("纠正数值后未恢复正确的图片＋视频结果。");
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        report["status"]="PASS";
    }
}
