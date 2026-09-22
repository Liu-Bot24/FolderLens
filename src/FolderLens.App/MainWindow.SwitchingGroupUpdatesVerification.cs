using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifySwitchingGroupUpdates(Dictionary<string,object> report)
    {
        const int groupSize=64,groupCount=40;
        var source=new VirtualResults(groupSize*groupCount,(_,_)=>throw new IOException("Synthetic rows have no source file."));results=source;
        var groups=Enumerable.Range(0,groupCount).Select(i=>new SnapshotGroup($"g{i}",$"Group {i}",0,groupSize,i*groupSize,groupSize,"scanning","all")).ToArray();
        BindBrowserResults(source,groups);Shell.UpdateLayout();await Task.Delay(60);
        var models=browserGroups!.ToDictionary(g=>g.Info.Id);
        var edits=groups.ToDictionary(g=>g.Id,_=>(IReadOnlyList<RangeEdit>)Array.Empty<RangeEdit>());
        var args=Environment.GetCommandLineArgs();int iterations=args.Contains("--verify-minimal")?10:100;
        for(int step=0;step<iterations;step++)
        {
            var option=step%2==0?DetailsMode:ThumbnailMode;
            if(!args.Contains("--verify-no-switch"))((IToggleProvider)new ToggleButtonAutomationPeer(option).GetPattern(PatternInterface.Toggle)).Toggle();
            Shell.UpdateLayout();await Task.Delay(15);
            // Force collection of detached native adapters as repeated scanning
            // allocations do, then publish a real remove/insert group reorder.
            if(!args.Contains("--verify-no-gc")){GC.Collect();GC.WaitForPendingFinalizers();}
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"switching-stage.json"),System.Text.Json.JsonSerializer.Serialize(new{step,phase="group-notification",details=DetailsMode.IsChecked,process=Environment.ProcessId}));
            groups=groups.Skip(1).Concat(groups.Take(1)).Select((g,i)=>g with{Start=i*groupSize}).ToArray();
            UpdateBrowserResults(source,groups,edits);Shell.UpdateLayout();await Task.Delay(15);
            if(ActiveBrowser.Items.Count!=groupSize*groupCount)throw new InvalidOperationException("切换期间分组发布丢失文件。");
            if(browserGroups!.Any(g=>!ReferenceEquals(g,models[g.Info.Id])))throw new InvalidOperationException("切换替换了分组模型。");
        }
        report["switches"]=args.Contains("--verify-no-switch")?0:iterations;report["groupPublications"]=iterations;report["status"]="PASS";
    }

    private async Task VerifySwitchingLiveScan(string source,byte[] png,Dictionary<string,object> report)
    {
        const int expected=12000;
        string fixture=Path.Combine(source,"scan-switching");
        await Task.Run(()=>
        {
            for(int group=0;group<80;group++)
            {
                string directory=Path.Combine(fixture,$"Group-{group:D2}");Directory.CreateDirectory(directory);
                for(int n=0;n<150;n++)File.WriteAllBytes(Path.Combine(directory,$"image-{n:D3}.png"),png);
            }
        });
        folderGrouping=new(true);UpdateGroupingButton();
        int switches=0,whileScanning=0,publications=0;
        verifyPublicationDuration=_=>publications++;
        var timer=DispatcherQueue.CreateTimer();timer.Interval=TimeSpan.FromMilliseconds(35);
        timer.Tick+=(_,_)=>
        {
            var option=DetailsMode.IsChecked==true?ThumbnailMode:DetailsMode;
            ((IToggleProvider)new ToggleButtonAutomationPeer(option).GetPattern(PatternInterface.Toggle)).Toggle();
            switches++;if(scanTask is {IsCompleted:false})whileScanning++;
        };
        timer.Start();
        try
        {
            await OpenRoot(fixture);if(scanTask is not null)await scanTask;
            await WaitUntil(()=>results?.Count==expected&&switches>=100,TimeSpan.FromSeconds(40));
            if(metadataTask is not null)await metadataTask;
            if(whileScanning<2||publications<2)throw new InvalidOperationException("没有覆盖扫描中的视图切换及多次列表发布。");
            if(ActiveBrowser.Items.Count!=expected||browserGroups?.Sum(g=>g.Info.Count)!=expected)throw new InvalidOperationException("扫描后文件或分组数量错误。");
            report["files"]=expected;report["switches"]=switches;report["switchesDuringScan"]=whileScanning;report["publications"]=publications;report["status"]="PASS";
        }
        finally{timer.Stop();verifyPublicationDuration=null;}
    }
}
