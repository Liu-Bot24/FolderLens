using System.Diagnostics;
using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    // Real sources are read only; all indexes and evidence remain in --data-dir.
    private async Task VerifyDirectorySource(string source,string first,string second,Dictionary<string,object> report)
    {
        source=PathRules.ValidateSource(source);first=PathRules.ValidateSource(first);second=PathRules.ValidateSource(second);
        string output=Path.GetFullPath(dataDirectory);
        if(DirectoryBrowseScope.Relative(source,output) is not null||
            DirectoryBrowseScope.Relative(source,first) is not {Length:>0}||DirectoryBrowseScope.Relative(source,second) is not {Length:>0})
            throw new ArgumentException("验证输出必须独立于源，两个目标必须是根目录的子目录。");
        var clock=Stopwatch.StartNew();double last=0;var gaps=new List<double>();var switches=new List<object>();var openings=new List<Task>();var slow=new List<double>();
        var publications=new List<object>();
        verifyPublicationDuration=elapsed=>publications.Add(new{atMs=clock.Elapsed.TotalMilliseconds,durationMs=elapsed,count=resultHandle?.Count,scope=lastAppliedFilter?.DirectoryScope});
        var heartbeat=DispatcherQueue.CreateTimer();heartbeat.Interval=TimeSpan.FromMilliseconds(50);
        heartbeat.Tick+=(_,_)=>{double now=clock.Elapsed.TotalMilliseconds;gaps.Add(now-last);last=now;};heartbeat.Start();
        async Task Stage(string stage)=>await File.WriteAllTextAsync(Path.Combine(dataDirectory,"directory-navigation-stage.txt"),stage);
        try
        {
            await Stage("opening-root");openings.Add(OpenRoot(source));
            await WaitUntil(()=>browserRootReady&&root==source&&resultHandle is {Count:>0}&&!queryBusy,TimeSpan.FromSeconds(30));
            report["initialResultCount"]=resultHandle!.Count;
            for(int phase=0;phase<2;phase++)
            {
                if(phase==1)
                {
                    await Stage("switching-already-discovered-targets");
                }
                for(int i=0;i<8;i++)
                {
                    string target=i%2==0?first:second;
                    await Stage($"switch-{phase}-{i}");double start=clock.Elapsed.TotalMilliseconds;long prior=queryRequest;
                    bool scanning=backgroundScans.Any(s=>!s.Completion.IsCompleted);
                    openings.Add(OpenRoot(target));
                    // An empty snapshot while discovery is in progress is a published result,
                    // not a stalled navigation. Record it separately from file discovery.
                    await WaitUntil(()=>BrowsedDirectory==target&&RootPath.Text==target&&queryRequest>prior&&resultHandle is not null&&
                        lastAppliedFilter is {} applied&&Path.Combine(root,applied.DirectoryScope)==target,TimeSpan.FromSeconds(20));
                    double elapsed=clock.Elapsed.TotalMilliseconds-start;
                    long firstPublishedCount=resultHandle!.Count;
                    await WaitUntil(()=>resultHandle is {Count:>0}&&lastAppliedFilter is {} applied&&Path.Combine(root,applied.DirectoryScope)==target,TimeSpan.FromSeconds(10));
                    double firstFilesMs=clock.Elapsed.TotalMilliseconds-start;
                    var handle=resultHandle!;string queryRoot=root;
                    var page=await catalog!.ReadPage(handle.Id,0,32,lifetime.Token);
                    if(page.Any(row=>DirectoryBrowseScope.Relative(target,Path.Combine(queryRoot,row.RelativePath)) is null))
                        throw new InvalidOperationException("真实目录切换显示了旧目录的文件。");
                    switches.Add(new{phase,index=i,elapsedMs=elapsed,firstPublishedCount,firstFilesMs,count=handle.Count,scanning,rowsChecked=page.Count});
                    if(elapsed>2000)slow.Add(elapsed);
                    if(firstFilesMs>5000)throw new InvalidOperationException("目标目录的首批文件等待超过五秒。");
                    await Task.Delay(250);
                }
            }
            if(slow.Count>0)throw new InvalidOperationException("真实目录切换显示结果超过两秒。");
            if(gaps.Count<5||gaps.Max()>2000)throw new InvalidOperationException("真实目录切换中 UI 消息处理停顿超过两秒。");
            await Stage("complete");report["status"]="PASS";
        }
        finally
        {
            heartbeat.Stop();report["switches"]=switches;report["uiHeartbeatMaxGapMs"]=gaps.DefaultIfEmpty().Max();
            report["publications"]=publications;verifyPublicationDuration=null;
            report["scanProgress"]=scanProgress.Values.Select(item=>item.Item1).ToArray();
            scanStop.Cancel();foreach(var scan in backgroundScans)scan.Cancel();
            await Task.WhenAll(openings).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private async Task VerifyDirectoryNavigation(string source,Dictionary<string,object> report)
    {
        byte[] png=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=0;i<3;i++)await File.WriteAllBytesAsync(Path.Combine(source,"B",$"other-{i}.png"),png);
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyCandidateBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
        var switching=OpenRoot(Path.Combine(source,"A"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            report["targetAddressWhileQueryPending"]=RootPath.Text==Path.Combine(source,"A");
            if(!(bool)report["targetAddressWhileQueryPending"])throw new InvalidOperationException("Pending child navigation still displays the scan root address.");
            report["targetTreeWhileQueryPending"]=(FolderTree.SelectedNode?.Content as FolderNode)?.Path==Path.Combine(source,"A");
            if(!(bool)report["targetTreeWhileQueryPending"])throw new InvalidOperationException("Pending child navigation still selects the scan root in the tree.");
        }
        finally{release.TrySetResult();verifyCandidateBarrier=null;await switching;}
        var times=new List<double>();
        for(int i=0;i<12;i++)
        {
            string child=i%2==0?"B":"A";int count=child=="A"?12:3;var watch=Stopwatch.StartNew();
            await OpenRoot(Path.Combine(source,child));
            if(resultHandle?.Count!=count||BrowsedDirectory!=Path.Combine(source,child)||RootPath.Text!=BrowsedDirectory)throw new InvalidOperationException("Directory navigation published another scope's results.");
            var page=await catalog!.ReadPage(resultHandle.Id,0,16,lifetime.Token);
            if(page.Any(item=>!item.RelativePath.StartsWith(child+"\\",StringComparison.Ordinal)))throw new InvalidOperationException("Old directory rows leaked into the new scope.");
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        report["scopeSwitchMilliseconds"]=times;
        if(times.Max()>2000)throw new InvalidOperationException("Local directory navigation took more than two seconds.");
        report["status"]="PASS";
    }
}
