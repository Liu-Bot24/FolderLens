using System.Diagnostics;
using FolderLens.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyCategorySwitch(string source,Dictionary<string,object> report)
    {
        source=PathRules.ValidateSource(source);
        string output=Path.GetFullPath(dataDirectory);
        if(output.Equals(source,StringComparison.OrdinalIgnoreCase)||output.StartsWith(source.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("验证目录必须独立。");
        folderGrouping=new(true);UpdateGroupingButton();
        suppressFilters=true;try{SelectTag(Category,"video");UpdateSortOptions();}finally{suppressFilters=false;}
        var gaps=new List<double>();var clock=Stopwatch.StartNew();double last=clock.Elapsed.TotalMilliseconds;
        var heartbeat=DispatcherQueue.CreateTimer();heartbeat.Interval=TimeSpan.FromMilliseconds(50);
        heartbeat.Tick+=(_,_)=>{double now=clock.Elapsed.TotalMilliseconds;gaps.Add(now-last);last=now;};heartbeat.Start();
        var switches=new List<object>();
        Task? opening=null;
        void Stage(string stage)=>File.WriteAllText(Path.Combine(dataDirectory,"category-switch-stage.txt"),stage);
        try
        {
            Stage("opening-video");opening=OpenRoot(source);
            await WaitUntil(()=>resultHandle is {Count:>0}&&!queryBusy,TimeSpan.FromSeconds(90));
            if(Environment.GetCommandLineArgs().Contains("--verify-category-large"))
            {
                Stage("waiting-for-95000-indexed-files");long files=0;
                do
                {
                    files=await catalog!.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files WHERE root_id=$root AND entry_state='present'";cmd.Parameters.AddWithValue("$root",rootId);return (long)cmd.ExecuteScalar()!;});
                    if(files>=95000||opening.IsCompleted)break;
                    if(clock.Elapsed>TimeSpan.FromSeconds(120))throw new TimeoutException("扫描未在两分钟内达到复现规模。");
                    await Task.Delay(500);
                }while(true);
                report["indexedFilesBeforeSwitch"]=files;report["scanElapsedBeforeSwitchMs"]=clock.Elapsed.TotalMilliseconds;
                if(files<95000)throw new InvalidOperationException("目录样本未达到截图的九万五千文件规模。");
                gaps.Clear();last=clock.Elapsed.TotalMilliseconds;
            }
            for(int i=0;i<6;i++)
            {
                string category=i%2==0?"audio":"video";Stage("switch-"+category+"-"+i);
                long before=queryRequest;double start=clock.Elapsed.TotalMilliseconds;
                Category.IsDropDownOpen=true;SelectTag(Category,category);
                await WaitUntil(()=>queryRequest>before&&!queryBusy,TimeSpan.FromSeconds(30));
                Category.IsDropDownOpen=false;
                if(resultHandle is null||results is null)throw new InvalidOperationException("切换没有发布结果。");
                var page=await catalog!.ReadPage(resultHandle.Id,0,16,lifetime.Token);
                if(page.Count==0||page.Any(item=>item.Kind!=category))throw new InvalidOperationException("类别切换未显示所选类型。");
                await Task.Delay(250);
                switches.Add(new{category,count=resultHandle.Count,elapsedMs=clock.Elapsed.TotalMilliseconds-start,scanning=scanTask is {IsCompleted:false}});
            }
            Stage("complete");report["switches"]=switches;report["uiHeartbeatMaxGapMs"]=gaps.DefaultIfEmpty().Max();
            if(gaps.Count<5||gaps.Max()>2000)throw new InvalidOperationException("切换中 UI 消息处理停顿超过两秒。");
            report["status"]="PASS";
        }
        finally
        {
            heartbeat.Stop();Category.IsDropDownOpen=false;report["switches"]=switches;
            report["uiHeartbeatMaxGapMs"]=gaps.DefaultIfEmpty().Max();
            scanStop.Cancel();if(opening is not null)await opening;
        }
    }
}
