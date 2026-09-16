using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
namespace FolderLens.App;
public sealed partial class MainWindow
{
    private BoundedDiagnosticLog? scanLog;
    private DispatcherTimer? scanLogTimer;
    private readonly Dictionary<string,(ScanProgress Progress,DateTimeOffset At)> scanProgress=new();
    private string? slowDatabaseOperation;
    private double slowDatabaseMilliseconds;
    private void InitializeScanDiagnostics()
    {
        scanLog=new(Path.Combine(dataDirectory,"logs"));
        scanLog.Write("startup",new{build=typeof(MainWindow).Assembly.GetName().Version?.ToString()});
        DatabaseExecutor.OperationMeasured=(operation,ms)=>{if(ms>=250){Volatile.Write(ref slowDatabaseOperation,operation);Volatile.Write(ref slowDatabaseMilliseconds,ms);}};
        scanLogTimer=new(){Interval=TimeSpan.FromSeconds(5)};
        scanLogTimer.Tick+=(_,_)=>
        {
            var counts=scanScheduler.Counts;
            var scans=backgroundScans.OrderByDescending(s=>s.RootId==rootId).Take(32).Select(scan=>new{root=scan.RootId,current=scan.RootId==rootId,waiting=scanScheduler.IsWaiting(scan.RootId),complete=scan.Completion.IsCompleted,cancelled=scan.Cancelled,files=scanProgress.TryGetValue(scan.RootId,out var p)?p.Progress.Files:0,secondsSinceProgress=scanProgress.TryGetValue(scan.RootId,out p)?(DateTimeOffset.UtcNow-p.At).TotalSeconds:(double?)null}).ToArray();
            // Diagnostics observe state, including the initial screen and an
            // unfinished filter edit. They must not validate a new query.
            scanLog.Write("scan-status",new{root,scope=advanced?.DirectoryScope??"",counts.Active,counts.Waiting,scans,queryBusy,
                displayed=resultHandle?.Count??firstPageSequence.Length,preview=PreviewDiagnosticState(),slowOperation=Volatile.Read(ref slowDatabaseOperation),slowMilliseconds=Volatile.Read(ref slowDatabaseMilliseconds)});
            if(!closing&&!replacingRoot&&browserScanError is null&&scanTask is {IsCompleted:false}&&!scanStop.IsCancellationRequested&&FilesGrid.Items.Count==0&&FilesList.Items.Count==0)Status.Text=ScanStatusDescription();
            UpdateBrowserEmptyState();
        };
        scanLogTimer.Start();
    }
    private string ScanStatusDescription()
    {
        if(activeBackgroundScan is not {} scan)return Status.Text;
        string scope=advanced?.DirectoryScope is {Length:>0}?"后台根目录扫描：":"";
        if(scanScheduler.IsWaiting(scan.RootId))return scope+"正在等待当前批次结束，即将继续扫描此文件夹。";
        if(scanProgress.TryGetValue(scan.RootId,out var p))
            return scope+$"已发现 {p.Progress.Files:N0} 个文件、{p.Progress.Directories:N0} 个文件夹"+((DateTimeOffset.UtcNow-p.At).TotalSeconds>=30?"；扫描暂未取得新进展，可停止或刷新。":"。");
        return scope+((DateTimeOffset.UtcNow-scan.RequestedAt).TotalSeconds>=30?"扫描尚未返回文件；可停止扫描或刷新重试。":"正在读取文件夹，尚未发现文件。");
    }
}
