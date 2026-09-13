using System.Diagnostics;
using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Action<string>? verifyPreviewStage;
    private Action<long>? verifyImageDrawn;
    private long verifyBitmapSelection=-1;
    private Func<FileRow,CancellationToken,Task>? verifyPrefetchBarrier;
    private bool verifyEncodedPrefetch;
    private bool verifyColdSwitch;
    private async Task VerifyPrefetchTurnaround(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        foreach(int ordinal in new[]{2,1,0,1}){await SelectPreview((FileRow)results![ordinal]!);if(prefetchTask is not null)await prefetchTask;}
        string next=Path.Combine(root,((FileRow)results![2]!).RelativePath);
        report["nextTargetRetained"]=prefetched.Any(item=>item.Path==next&&item.Bitmap is not null);
        if(!(bool)report["nextTargetRetained"])throw new InvalidOperationException("反向后另一侧预取淘汰了下一张刚命中的缓存。");
        report["status"]="PASS";
    }
    private async Task VerifyPreparedCache(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectPreview((FileRow)results![0]!);if(prefetchTask is not null)await prefetchTask;
        var next=(FileRow)results[1]!;
        var cached=prefetched.Single(item=>item.Path==Path.Combine(root,next.RelativePath));
        var prepared=cached.Bitmap??throw new InvalidOperationException("预取没有生成可绘制位图。");
        await SelectPreview(next);
        if(!ReferenceEquals(fitBitmap,prepared)||cached.Bitmap is not null)throw new InvalidOperationException("缓存没有把位图所有权交给当前图。");
        report["ownershipTransferred"]=true;
        if(prefetchTask is not null)await prefetchTask;
        var disposable=prefetched.Where(item=>item.Bitmap is not null).Select(item=>item.Bitmap!).ToArray();
        OnPrefetchMemoryPressure();await WaitUntil(()=>Volatile.Read(ref prefetchPressurePending)==0,TimeSpan.FromSeconds(3));
        if(prefetched.Count!=0||preparedPrefetchBytes!=0||!ReferenceEquals(fitBitmap,prepared)||prepared.GetPixelBytes().Length==0)
            throw new InvalidOperationException("压力清理破坏当前图或泄漏缓存计数。");
        foreach(var bitmap in disposable)
        {
            bool disposed=false;try{bitmap.GetPixelBytes();}catch(ObjectDisposedException){disposed=true;}
            if(!disposed)throw new InvalidOperationException("压力清理没有释放缓存位图。");
        }
        report["pressureDisposedCachedBitmaps"]=disposable.Length;
        SchedulePrefetch(selection);if(prefetchTask is not null)await prefetchTask;
        var stale=prefetched.First();var staleRow=results.Cast<FileRow>().First(row=>Path.Combine(root,row.RelativePath)==stale.Path);
        File.SetLastWriteTimeUtc(stale.Path,File.GetLastWriteTimeUtc(stale.Path).AddSeconds(2));
        if(await FindPrefetched(staleRow,256,256,lifetime.Token) is not null||stale.Bitmap is not null)
            throw new InvalidOperationException("源版本改变后仍返回缓存。");
        report["changedSourceRejected"]=true;
        // Exercise the real NewDevice callback by replacing this control's device.
        // This does not simulate a hardware driver failure or change system settings.
        using(var replacement=new Microsoft.Graphics.Canvas.CanvasDevice())
        {
            var original=ImageCanvas.CustomDevice;long deviceRevision=imageResourceRevision;
            try
            {
                ImageCanvas.CustomDevice=replacement;ImageCanvas.Invalidate();
                await WaitUntil(()=>imageResourceRevision>deviceRevision&&!previewLoading&&fitBitmap is not null&&previewReadySelection==selection,TimeSpan.FromSeconds(15));
                if(prefetched.Any(item=>item.Bitmap is not null&&item.DeviceRevision!=imageResourceRevision))throw new InvalidOperationException("设备重建保留了旧设备位图。");
                report["deviceReplacementRebuilt"]=true;
            }
            finally
            {
                deviceRevision=imageResourceRevision;ImageCanvas.CustomDevice=original;ImageCanvas.Invalidate();
                await WaitUntil(()=>imageResourceRevision>deviceRevision&&!previewLoading&&fitBitmap is not null&&previewReadySelection==selection,TimeSpan.FromSeconds(15));
            }
        }
        await OpenRoot(Path.Combine(source,"B"));
        if(prefetched.Count!=0||preparedPrefetchBytes!=0)throw new InvalidOperationException("切根没有清理预取位图。");
        report["rootChangeClearedCache"]=true;report["status"]="PASS";
    }
    private async Task VerifyWheelDistance(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);await SetImmersive(true);
        wheelRemainder=0;var checks=new List<object>();var failures=new List<string>();
        void Check(int delta,int expected)
        {
            NavigateViewerWheel(delta);bool passed=selected?.Ordinal==expected;
            checks.Add(new{delta,expected,actual=selected?.Ordinal,passed});if(!passed)failures.Add($"{delta}: {selected?.Ordinal} != {expected}");
        }
        Check(-1200,10);Check(240,8);Check(-60,8);Check(-60,9);Check(-1200,11);Check(2400,0);
        report["checks"]=checks;report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        await WaitUntil(()=>!previewLoading&&previewReadySelection==selection,TimeSpan.FromSeconds(10));
        report["status"]="PASS";
    }
    private async Task VerifyPrefetchAdoption(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        prefetchStop.Cancel();if(prefetchTask is not null)await prefetchTask;ClearPrefetchedImages();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyPrefetchBarrier=async(row,token)=>{if(row.Ordinal==1){entered.TrySetResult();await release.Task.WaitAsync(token);}};
        try
        {
            await SelectPreview((FileRow)results![0]!);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stages=new List<string>();verifyPreviewStage=stages.Add;
            var next=SelectPreview((FileRow)results[1]!);release.TrySetResult();await next.WaitAsync(TimeSpan.FromSeconds(10));
            report["stages"]=stages;report["targetReady"]=selected?.Ordinal==1&&previewReadySelection==selection&&!previewLoading&&fitBitmap is not null;
            if(!stages.Contains("prefetchHit")||stages.Contains("workerFit")||!(bool)report["targetReady"])
                throw new InvalidOperationException("当前目标没有接管正在进行的邻图预取，而是重新解码。");
            report["status"]="PASS";
        }
        finally{release.TrySetResult();verifyPrefetchBarrier=null;verifyPreviewStage=null;}
    }

    private async Task VerifyImageSwitch(string source,Dictionary<string,object> report)
    {
        source=PathRules.ValidateSource(source);
        if(Path.GetFullPath(dataDirectory).StartsWith(source.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("验证输出必须位于源目录以外。");
        var samples=new List<object>();report["samples"]=samples;
        verifyEncodedPrefetch=Environment.GetCommandLineArgs().Contains("--verify-encoded-prefetch");
        verifyColdSwitch=Environment.GetCommandLineArgs().Contains("--verify-cold-switch");
        report["applicationCacheCold"]=verifyColdSwitch;
        report["prefetchRepresentation"]=verifyEncodedPrefetch?"PNG only, bitmap preparation disabled for comparison":"PNG plus prepared CanvasBitmap";
        report["timingEndpoint"]="First CanvasControl Draw of the target bitmap identity; not display Present";
        await OpenRoot(source).WaitAsync(TimeSpan.FromSeconds(60));
        if(scanTask is not null)await scanTask.WaitAsync(TimeSpan.FromSeconds(60));
        await RefreshQuery();
        if(results is null||results.Count<12)throw new InvalidOperationException("切图验证需要至少十二张图片。");
        // The real viewer entry requires a selected file. An empty selection silently
        // stays in the sidebar and must never masquerade as a large-view benchmark.
        await SelectPreview((FileRow)results[0]!);await SetImmersive(true);Shell.UpdateLayout();
        if(!immersive||ImageCanvas.ActualWidth<Shell.ActualWidth*.8||ImageCanvas.ActualHeight<Shell.ActualHeight*.6)
            throw new InvalidOperationException("切图测试未进入实际大图查看布局。");
        prefetchStop.Cancel();if(prefetchTask is not null)await prefetchTask;ClearPrefetchedImages();ClearImage();
        report["viewport"]=new{immersive,width=ImageCanvas.ActualWidth,height=ImageCanvas.ActualHeight,raster=Shell.XamlRoot.RasterizationScale,windowWidth=AppWindow.Size.Width,windowHeight=AppWindow.Size.Height};
        report["warmup"]="Selected ordinal 0 to enter viewer; measured sequence starts at ordinal 1 with prefetch cleared. OS cache not cleared.";
        var resources=new List<object>();report["resources"]=resources;
        var clock=new Stopwatch();var stages=new List<object>();
        verifyPreviewStage=name=>stages.Add(new{name,ms=clock.Elapsed.TotalMilliseconds});
        try
        {
            int[] ordinals=Enumerable.Range(1,111).Select(sample=>sample%22<=11?sample%22:22-sample%22).ToArray();
            for(int sample=0;sample<ordinals.Length;sample++)
            {
                int index=ordinals[sample];
                stages=[];clock.Restart();
                var row=(FileRow)results[index]!;
                long expectedSelection=selection+1;
                var drawn=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                verifyImageDrawn=id=>{if(id==expectedSelection)drawn.TrySetResult(clock.Elapsed.TotalMilliseconds);};
                await SelectPreview(row).WaitAsync(TimeSpan.FromSeconds(15));
                double ready=clock.Elapsed.TotalMilliseconds;
                if(previewReadySelection!=selection||previewLoading||fitBitmap is null)
                    throw new InvalidOperationException("切图结束但目标图片未显示。");
                ImageCanvas.Invalidate();double drawMs=await drawn.Task.WaitAsync(TimeSpan.FromSeconds(5));
                samples.Add(new{index=sample,ordinal=index,phase=verifyColdSwitch?"applicationCacheCold":sample<=6?"rapid":"afterPrefetchWait",elapsedMs=ready,targetDrawMs=drawMs,stages=stages.ToArray()});
                if(sample%10==0||sample==ordinals.Length-1)
                {
                    using var process=Process.GetCurrentProcess();var budget=WorkerResources.Shared.Snapshot;
                    resources.Add(new{sample,appPrivateBytes=process.PrivateMemorySize64,appHandles=process.HandleCount,appThreads=process.Threads.Count,appCpuSeconds=process.TotalProcessorTime.TotalSeconds,budget.ProcessTreeBytes,budget.MeasurementComplete,preparedPrefetchBytes,prefetchBytes});
                }
                Console.WriteLine($"Switch {index}: {ready:F1}ms {System.Text.Json.JsonSerializer.Serialize(stages)}");
                // First half mimics repeated switches; second half allows prefetch to finish.
                if(sample>=6&&prefetchTask is not null)await prefetchTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            report["status"]="PASS";
        }
        finally{verifyPreviewStage=null;verifyImageDrawn=null;verifyEncodedPrefetch=false;verifyColdSwitch=false;}
    }
}
