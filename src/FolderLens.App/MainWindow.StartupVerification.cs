using System.Diagnostics;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Stopwatch? startupClock;
    private double startupStageAt,startupUiAt;
    private double startupShellLoadedAt=-1;
    private readonly Dictionary<string,double> startupStages=[];
    private readonly List<double> startupUiGaps=[];
    private readonly List<double> startupLoadedUiGaps=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,(int Count,double Total,double Maximum)> startupDatabase=[];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? startupHeartbeat;
    private void StartStartupMeasurements()
    {
        if(!Environment.GetCommandLineArgs().Contains("--verify-startup-profile"))return;
        FolderLens.Infrastructure.DatabaseExecutor.OperationMeasured=(name,ms)=>startupDatabase.AddOrUpdate(name,(1,ms,ms),(_,prior)=>(prior.Count+1,prior.Total+ms,Math.Max(prior.Maximum,ms)));
        FolderLens.Infrastructure.CatalogStore.OperationMigrationMeasured=(name,ms)=>startupDatabase.AddOrUpdate("migration."+name,(1,ms,ms),(_,prior)=>(prior.Count+1,prior.Total+ms,Math.Max(prior.Maximum,ms)));
        startupClock=Stopwatch.StartNew();startupHeartbeat=DispatcherQueue.CreateTimer();startupHeartbeat.Interval=TimeSpan.FromMilliseconds(16);
        startupHeartbeat.Tick+=(_,_)=>
        {
            double now=startupClock.Elapsed.TotalMilliseconds;
            if(startupUiGaps.Count<4096)startupUiGaps.Add(now-startupUiAt);
            if(startupShellLoadedAt>=0&&startupLoadedUiGaps.Count<4096)startupLoadedUiGaps.Add(now-Math.Max(startupUiAt,startupShellLoadedAt));
            startupUiAt=now;
        };startupHeartbeat.Start();
    }
    private void StartupStage(string name)
    {
        if(startupClock is null)return;double now=startupClock.Elapsed.TotalMilliseconds;startupStages[name]=now-startupStageAt;startupStageAt=now;
    }
    private async Task VerifyStartupProfile(string source,Dictionary<string,object> report)
    {
        StartupStage("verificationFixture");
        report["initializationStagesMs"]=startupStages.ToDictionary(p=>p.Key,p=>p.Value);
        report["initializationMs"]=startupStages.Where(p=>p.Key!="verificationFixture").Sum(p=>p.Value);
        var initializeGaps=startupUiGaps.ToArray();var loadedGaps=startupLoadedUiGaps.ToArray();startupUiGaps.Clear();
        if(WindowFocus.IsForeground(this))throw new InvalidOperationException("验证窗口意外位于前台。");
        var workTimer=Stopwatch.StartNew();var workStages=new Dictionary<string,double>();
        double last=0;void Stage(string name){double now=workTimer.Elapsed.TotalMilliseconds;workStages[name]=now-last;last=now;}
        await OpenRoot(source);Stage("openRootAndScan");if(metadataTask is not null)await metadataTask;Stage("metadata");await RefreshQuery();Stage("refreshQuery");
        if(results?.Count!=12)throw new InvalidOperationException("窗口不在前台时扫描没有完成。");
        var row=(FileRow)results[0]!;await results.EnsureLoaded(row,lifetime.Token);await SelectPreview(row);
        Stage("selectPreview");report["backgroundWorkStagesMs"]=workStages;
        await ReadDemandedMetadata(row,lifetime.Token);selectedProperties=await ResolveRow(row,rootId,lifetime.Token);
        if(selectedProperties.Width!=64)throw new InvalidOperationException("选中文件的按需元数据不正确。");
        if(fitBitmap is null||sourceWidth!=64||sourceHeight!=48||WindowFocus.IsForeground(this))throw new InvalidOperationException("后台元数据或预览未完成，或窗口抢占了前台。");
        startupHeartbeat?.Stop();using var process=Process.GetCurrentProcess();
        report["initializationUiGapMaxMs"]=initializeGaps.DefaultIfEmpty().Max();
        report["shellLoadedAtMs"]=startupShellLoadedAt;
        report["loadedShellUiGapMaxMs"]=loadedGaps.DefaultIfEmpty().Max();
        report["backgroundWorkUiGapMaxMs"]=startupUiGaps.DefaultIfEmpty().Max();
        report["mainCpuMs"]=process.TotalProcessorTime.TotalMilliseconds;report["mainPeakBytes"]=process.PeakWorkingSet64;
        report["databaseOperations"]=startupDatabase.OrderByDescending(p=>p.Value.Total).Take(20).Select(p=>new{operation=p.Key,count=p.Value.Count,totalMs=p.Value.Total,maxMs=p.Value.Maximum}).ToArray();
        report["backgroundScanFiles"]=results.Count;report["backgroundMetadataAndPreview"]=true;
        // Window construction precedes the Loaded/input boundary. Retain its raw
        // timing separately rather than calling it a stall in an interactive shell.
        bool responsive=loadedGaps.DefaultIfEmpty().Max()<200&&startupUiGaps.DefaultIfEmpty().Max()<200;
        report["responsiveBelow200ms"]=responsive;
        if(!responsive)throw new InvalidOperationException("启动或后台加载期间 UI 心跳间隔超过 200ms，保留分阶段诊断。");
        report["status"]="PASS";
    }
}
