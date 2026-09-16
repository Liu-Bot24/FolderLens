using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifySoak(string source,Dictionary<string,object> report)
    {
        bool retentionCheck=Environment.GetCommandLineArgs().Contains("--verify-audio-retention");
        bool smoke=Environment.GetCommandLineArgs().Contains("--verify-soak-smoke")||(retentionCheck&&!Environment.GetCommandLineArgs().Contains("--verify-soak"));
        bool fast=Environment.GetCommandLineArgs().Contains("--verify-soak-fast");
        bool imagesOnly=Environment.GetCommandLineArgs().Contains("--verify-soak-images-only");
        string mix=Environment.GetCommandLineArgs().FirstOrDefault(arg=>arg.StartsWith("--verify-soak-mix=",StringComparison.Ordinal))?.Split('=')[1]??"all";
        if(mix is not ("all" or "filters" or "text" or "markdown" or "audio"))throw new ArgumentException("Unknown diagnostic mix.");
        int required=smoke?24:10_000;var duration=TimeSpan.FromMinutes(smoke||fast?0:30);
        report["diagnosticOnly"]=fast||imagesOnly||mix!="all";report["diagnosticMix"]=mix;
        report["imagesOnly"]=imagesOnly;
        await File.WriteAllTextAsync(Path.Combine(source,"soak.txt"),string.Concat(Enumerable.Repeat("中文与 emoji 🌏 / bounded reader\r\n",3000)));
        await File.WriteAllTextAsync(Path.Combine(source,"soak.md"),"# 稳定性测试\n\n正文与 **格式**。\n\n![图片](A/image-00.png)\n");
        using(var stream=File.Create(Path.Combine(source,"soak.wav")))using(var writer=new BinaryWriter(stream,Encoding.ASCII))
        {
            const int size=16000*2;writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+size);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(8000);writer.Write(16000);writer.Write((short)2);writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(size);writer.Write(new byte[size]);
        }
        suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;AudioMute.IsChecked=true;
        await OpenRoot(source);await RefreshQuery();
        // Exercise a delayed dispatcher/debounce without blocking the desktop.
        // This affects only this isolated verification instance.
        bool delayedSearch=Environment.GetCommandLineArgs().Contains("--verify-soak-delayed-search");
        if(delayedSearch)searchTimer!.Interval=TimeSpan.FromMilliseconds(600);
        var samples=new List<object>();report["resources"]=samples;
        var retiredAudioPlayers=new List<WeakReference<MediaPlayer>>();
        var clock=Stopwatch.StartNew();int switches=0,mixedCycles=0;
        object MemoryObservation()
        {
            using var process=Process.GetCurrentProcess();var heap=GC.GetGCMemoryInfo();
            return new{privateBytes=process.PrivateMemorySize64,managedBytes=GC.GetTotalMemory(false),heap.HeapSizeBytes,heap.TotalCommittedBytes,heap.FragmentedBytes,gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2)};
        }
        report["memoryAtStart"]=MemoryObservation();
        async Task SelectPath(string path)
        {
            var handle=resultHandle??throw new InvalidOperationException("Soak result missing.");var sourceResults=results!;long request=queryRequest;
            long ordinal=await catalog!.FindOrdinal(handle.Id,path)??throw new InvalidOperationException("Soak fixture missing: "+path);
            if(request!=queryRequest||!ReferenceEquals(handle,resultHandle)||!ReferenceEquals(sourceResults,results))throw new InvalidOperationException("Soak query changed during ordinal lookup.");
            if(!await SelectBrowserOrdinal(sourceResults,checked((int)ordinal),lifetime.Token)||previewReadySelection!=selection||previewFailure is not null)
                throw new InvalidOperationException("Soak preview failed: "+path+" / "+QualityLabel.Text);
        }
        async Task SearchFor(string text)
        {
            long before=queryRequest;
            Search.Text=text;
            // TextChanged and the debounce tick are dispatched asynchronously.
            // Elapsed time alone does not prove the new filter was published.
            await WaitUntil(()=>queryRequest>before&&searchTimer?.IsRunning!=true&&!queryBusy&&
                lastAppliedFilter?.NamePathQuery==text,TimeSpan.FromSeconds(30));
            report["lastSearchWait"]=new{text,before,after=queryRequest,timerRunning=searchTimer?.IsRunning,queryBusy,applied=lastAppliedFilter?.NamePathQuery};
        }
        try
        {
            while(switches<required||clock.Elapsed<duration)
            {
                var step=Stopwatch.StartNew();
                await SelectPath(Path.Combine("A",$"image-{switches%12:D2}.png"));
                await SetImmersive(true);
                if(fitBitmap is null)throw new InvalidOperationException("Soak image has no drawable bitmap.");
                switches++;
                if(switches%(smoke?12:100)==0)
                {
                    if(!imagesOnly)
                    {
                        await ReturnToBrowser();
                        if(mix is "all" or "filters")
                        {
                        await SearchFor("image-0");
                        if(results?.Count!=10)throw new InvalidOperationException("Soak filter result mismatch.");
                        await SearchFor("");
                        }
                        if(mix is "all" or "text")
                        {
                        await SelectPath("soak.txt");await SetImmersive(true);await PageReader(1);
                        if(displayedText is null||TextContent.Text.Length==0)throw new InvalidOperationException("Soak text is empty.");
                        }
                        if(mix is "all" or "markdown")
                        {
                        await SelectPath("soak.md");
                        if(MarkdownHost.Visibility!=Visibility.Visible)throw new InvalidOperationException("Soak Markdown fell back unexpectedly.");
                        }
                        if(mix is "all" or "audio")
                        {
                        await SelectPath("soak.wav");PlayAudio(this,new());
                        await WaitUntil(()=>audio?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing,TimeSpan.FromSeconds(8));
                        retiredAudioPlayers.Add(new(audio!));
                        await WaitUntil(()=>audio!.PlaybackSession.Position.TotalMilliseconds>100,TimeSpan.FromSeconds(5));StopAudio();
                        }
                        await ReturnToBrowser();mixedCycles++;
                    }
                    using var process=Process.GetCurrentProcess();var budget=WorkerResources.Shared.Snapshot;
                    samples.Add(new{switches,mixedCycles,elapsedMs=clock.Elapsed.TotalMilliseconds,appPrivateBytes=process.PrivateMemorySize64,handles=process.HandleCount,threads=process.Threads.Count,budget.ProcessTreeBytes,budget.MeasurementComplete,budget.HardLimitBytes,prefetchBytes,preparedPrefetchBytes,memory=MemoryObservation()});
                    await File.WriteAllTextAsync(Path.Combine(dataDirectory,"soak-progress.json"),JsonSerializer.Serialize(new{status="RUNNING",switches,mixedCycles,elapsedMs=clock.Elapsed.TotalMilliseconds,samples}));
                    Console.WriteLine($"Soak: {switches} images, {mixedCycles} mixed cycles, {clock.Elapsed.TotalMinutes:F1} min, tree {budget.ProcessTreeBytes/1024/1024} MiB");
                    if(budget.MeasurementComplete&&budget.ProcessTreeBytes>budget.HardLimitBytes)throw new InvalidOperationException("Soak process tree exceeded its hard limit.");
                    if(prefetchBytes>32L*1024*1024||preparedPrefetchBytes>PreparedPrefetchLimit)throw new InvalidOperationException("Soak prefetch cache exceeded its limit.");
                }
                if(!smoke&&!fast&&step.ElapsedMilliseconds<180)await Task.Delay(180-(int)step.ElapsedMilliseconds,lifetime.Token);
            }
            report["switches"]=switches;report["mixedCycles"]=mixedCycles;report["durationMs"]=clock.Elapsed.TotalMilliseconds;
            if(Environment.GetCommandLineArgs().Contains("--verify-soak-memory")||retentionCheck)
            {
                await Task.Delay(5000,lifetime.Token);report["idleBeforeDiagnosticGc"]=MemoryObservation();
                // Diagnostic only, after the measured workload. Never collect
                // during switching to conceal sustained growth in normal operation.
                await Task.Run(()=>{GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,true,true);});
                await Task.Delay(1000,lifetime.Token);report["idleAfterDiagnosticGc"]=MemoryObservation();
                report["retiredAudioPlayers"]=new{observed=retiredAudioPlayers.Count,aliveAfterDiagnosticGc=retiredAudioPlayers.Count(reference=>reference.TryGetTarget(out _))};
                if(retentionCheck&&retiredAudioPlayers.Any(reference=>reference.TryGetTarget(out _)))
                    throw new InvalidOperationException("Disposed audio players remain reachable after idle and full diagnostic GC.");
            }
            report["scope"]=imagesOnly?"Diagnostic image-only run over generated 64x48 images; no mixed-operation or 30-minute acceptance claim.":"Generated 64x48 images, text, local Markdown image, silent WAV; real WinUI lifecycle. No physical sleep/network/device removal, GPU allocation or large-media endurance claim.";
            if(fast)report["pacing"]="Diagnostic unpaced run; not the required 30-minute soak.";
            report["smokeOnly"]=smoke;report["status"]="PASS";
        }
        finally{StopAudio();}
    }
}
