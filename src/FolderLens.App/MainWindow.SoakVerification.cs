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
        bool smoke=Environment.GetCommandLineArgs().Contains("--verify-soak-smoke");
        int required=smoke?24:10_000;var duration=TimeSpan.FromMinutes(smoke?0:30);
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
        var samples=new List<object>();report["resources"]=samples;
        var clock=Stopwatch.StartNew();int switches=0,mixedCycles=0;
        object MemoryObservation()
        {
            using var process=Process.GetCurrentProcess();var heap=GC.GetGCMemoryInfo();
            return new{privateBytes=process.PrivateMemorySize64,managedBytes=GC.GetTotalMemory(false),heap.HeapSizeBytes,heap.TotalCommittedBytes,heap.FragmentedBytes,gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2)};
        }
        report["memoryAtStart"]=MemoryObservation();
        async Task SelectPath(string path)
        {
            long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,path)??throw new InvalidOperationException("Soak fixture missing: "+path);
            if(!await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token)||previewReadySelection!=selection||previewFailure is not null)
                throw new InvalidOperationException("Soak preview failed: "+path+" / "+QualityLabel.Text);
        }
        async Task SearchFor(string text)
        {
            Search.Text=text;
            // TextChanged is queued by WinUI. Wait for the actual debounce/query
            // path as a user would, instead of racing it with a second query.
            await Task.Delay(350,lifetime.Token);
            if(queryBusy)await queryCompletion;
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
                    await ReturnToBrowser();await SearchFor("image-0");
                    if(results?.Count!=10)throw new InvalidOperationException("Soak filter result mismatch.");
                    await SearchFor("");
                    await SelectPath("soak.txt");await SetImmersive(true);await PageReader(1);
                    if(displayedText is null||TextContent.Text.Length==0)throw new InvalidOperationException("Soak text is empty.");
                    await SelectPath("soak.md");
                    if(MarkdownHost.Visibility!=Visibility.Visible)throw new InvalidOperationException("Soak Markdown fell back unexpectedly.");
                    await SelectPath("soak.wav");PlayAudio(this,new());
                    await WaitUntil(()=>audio?.PlaybackSession.PlaybackState==MediaPlaybackState.Playing,TimeSpan.FromSeconds(8));
                    await WaitUntil(()=>audio!.PlaybackSession.Position.TotalMilliseconds>100,TimeSpan.FromSeconds(5));StopAudio();
                    await ReturnToBrowser();mixedCycles++;
                    using var process=Process.GetCurrentProcess();var budget=WorkerResources.Shared.Snapshot;
                    samples.Add(new{switches,mixedCycles,elapsedMs=clock.Elapsed.TotalMilliseconds,appPrivateBytes=process.PrivateMemorySize64,handles=process.HandleCount,threads=process.Threads.Count,budget.ProcessTreeBytes,budget.MeasurementComplete,budget.HardLimitBytes,prefetchBytes,preparedPrefetchBytes,memory=MemoryObservation()});
                    await File.WriteAllTextAsync(Path.Combine(dataDirectory,"soak-progress.json"),JsonSerializer.Serialize(new{status="RUNNING",switches,mixedCycles,elapsedMs=clock.Elapsed.TotalMilliseconds,samples}));
                    Console.WriteLine($"Soak: {switches} images, {mixedCycles} mixed cycles, {clock.Elapsed.TotalMinutes:F1} min, tree {budget.ProcessTreeBytes/1024/1024} MiB");
                    if(budget.MeasurementComplete&&budget.ProcessTreeBytes>budget.HardLimitBytes)throw new InvalidOperationException("Soak process tree exceeded its hard limit.");
                    if(prefetchBytes>32L*1024*1024||preparedPrefetchBytes>PreparedPrefetchLimit)throw new InvalidOperationException("Soak prefetch cache exceeded its limit.");
                }
                if(!smoke&&step.ElapsedMilliseconds<180)await Task.Delay(180-(int)step.ElapsedMilliseconds,lifetime.Token);
            }
            report["switches"]=switches;report["mixedCycles"]=mixedCycles;report["durationMs"]=clock.Elapsed.TotalMilliseconds;
            if(Environment.GetCommandLineArgs().Contains("--verify-soak-memory"))
            {
                await Task.Delay(5000,lifetime.Token);report["idleBeforeDiagnosticGc"]=MemoryObservation();
                // Diagnostic only, after the measured workload. Never collect
                // during switching to conceal sustained growth in normal operation.
                await Task.Run(()=>{GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,true,true);});
                await Task.Delay(1000,lifetime.Token);report["idleAfterDiagnosticGc"]=MemoryObservation();
            }
            report["scope"]="Generated 64x48 images, text, local Markdown image, silent WAV; real WinUI lifecycle. No physical sleep/network/device removal, GPU allocation or large-media endurance claim.";
            report["smokeOnly"]=smoke;report["status"]="PASS";
        }
        finally{StopAudio();}
    }
}
