using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFirstPage(string source,Dictionary<string,object> report)
    {
        string sample=Directory.EnumerateFiles(source,"*.png",SearchOption.AllDirectories).First();
        for(int i=0;i<12;i++)File.Copy(sample,Path.Combine(source,$"first-page-{i:D2}.png"),true);
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await ResetFirstPageFixture();
        object? binding=null;FileRow[] rows=[];object?[] images=[],containers=[];int publications=0;
        verifyFirstPageBarrier=async token=>
        {
            publications++;binding=FilesGrid.ItemsSource;rows=FilesGrid.Items.Cast<FileRow>().ToArray();
            Shell.UpdateLayout();var timer=Stopwatch.StartNew();
            await WaitUntil(()=>rows.All(row=>row.Thumbnail is not null),TimeSpan.FromSeconds(10));
            images=rows.Select(row=>(object?)row.Thumbnail).ToArray();containers=rows.Select(row=>FilesGrid.ContainerFromItem(row)).ToArray();
            report["firstThumbnailMs"]=timer.Elapsed.TotalMilliseconds;
            if(resultHandle is not null)throw new InvalidOperationException("首批检查已晚于完整快照。");
        };
        void AssertRetained(string phase)
        {
            Shell.UpdateLayout();
            if(!ReferenceEquals(binding,FilesGrid.ItemsSource))throw new InvalidOperationException(phase+" 替换了 ItemsSource。");
            for(int i=0;i<rows.Length;i++)
                if(!ReferenceEquals(images[i],rows[i].Thumbnail)||!ReferenceEquals(containers[i],FilesGrid.ContainerFromItem(rows[i])))
                    throw new InvalidOperationException(phase+" 清空了已显示缩略图或容器。");
        }
        try
        {
            verifyCandidateBarrier=_=>throw new IOException("InjectedSnapshotFailure");
            await RefreshQuery();AssertRetained("第一次失败");
            await RefreshQuery(scanPreview:true);AssertRetained("第二次失败");
            string diagnostic=await File.ReadAllTextAsync(Path.Combine(RuntimeDataDirectory,"query-failures.jsonl"));
            if(diagnostic.Split('\n').Count(line=>line.Contains("\"errorType\":\"IOException\""))<2||diagnostic.Contains(source,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("查询失败历史未保留，或记录了源文件夹完整路径。");
            report["failureHistoryRetainedWithoutSourcePaths"]=true;
            if(publications!=1)throw new InvalidOperationException("重试重复发布首批列表。");
            FileRow? chosen=null;Task? coalesced=null;
            verifyCandidateBarrier=async token=>
            {
                // Inject one coalesced refresh. Reinjecting on its follow-up
                // snapshot creates an endless stream of test-generated refreshes.
                verifyCandidateBarrier=null;
                FilesGrid.SelectedItem=rows[0];await WaitUntil(()=>ReferenceEquals(selected,rows[0]),TimeSpan.FromSeconds(3));
                Navigate(1);chosen=rows[1];await WaitUntil(()=>ReferenceEquals(selected,chosen),TimeSpan.FromSeconds(3));
                long request=queryRequest;coalesced=RefreshQuery(scanPreview:true);await Task.Yield();
                if(queryRequest!=request||token.IsCancellationRequested)throw new InvalidOperationException("自动查询取消了同一候选快照。");
            };
            await RefreshQuery(scanPreview:true);if(coalesced is not null)await coalesced;AssertRetained("快照升级");
            if(resultHandle is null||results is null||!ReferenceEquals(selected,chosen)||results.IndexOf(chosen)<0)throw new InvalidOperationException("首批选择阻断升级或未保留选择。");
            Navigate(1);await WaitUntil(()=>ReferenceEquals(selected,rows[2]),TimeSpan.FromSeconds(3));
            report["retryRetainsRowsContainersAndThumbnails"]=true;report["promotionRetainsSelection"]=true;report["firstPageNavigation"]=true;report["automaticQueryCoalesced"]=true;report["status"]="PASS";
        }
        finally{verifyFirstPageBarrier=null;verifyCandidateBarrier=null;}
    }
}
