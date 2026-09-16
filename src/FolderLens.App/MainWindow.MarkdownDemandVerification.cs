using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyMarkdownDemand(string source,Dictionary<string,object> report)
    {
        string video=Path.Combine(source,"clip.mp4");
        await BoundedProcess.Run(Path.Combine(AppContext.BaseDirectory,"native","ffmpeg","ffmpeg.exe"),
            ["-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=640x360:rate=24","-t","8","-c:v","libx264","-crf","18","-pix_fmt","yuv420p",video],TimeSpan.FromSeconds(15),1024*1024,lifetime.Token);
        string body="# Demand reader\n\n![first](A/image-00.png)\n\n![clip](clip.mp4)\n\n"+
            string.Concat(Enumerable.Range(0,160).Select(i=>$"Paragraph {i}: offscreen media must not delay the document.\n\n"))+
            "![last](A/image-01.png)\n\n![escape](../outside.png)\n\n![remote](https://tracker.invalid/remote.png)\n";
        await File.WriteAllTextAsync(Path.Combine(source,"demand.md"),body);
        await File.WriteAllTextAsync(Path.Combine(source,"plain.txt"),"Leave the media document.");
        suppressFilters=true;SelectTag(Category,"text");suppressFilters=false;
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var requests=new List<string>();
        report["resourceRequests"]=requests;
        verifyMarkdownResourceBarrier=async(path,token)=>
        {
            requests.Add(path);
            if(path=="A/image-00.png"&&!release.Task.IsCompleted)
            {
                report["imageStartedBeforeNavigationCompleted"]=navigationComplete is {Task.IsCompleted:false};
                await release.Task.WaitAsync(token);
            }
        };
        try
        {
            long ordinal=(await catalog!.FindOrdinal(resultHandle!.Id,"demand.md"))!.Value;
            await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
            report["webviewEvents"]=webviewEvents.ToArray();report["quality"]=QualityLabel.Text;
            if(markdown?.CoreWebView2 is not {} core||MarkdownHost.Visibility!=Visibility.Visible)throw new InvalidOperationException("Document did not display.");
            report["initialDom"]=await core.ExecuteScriptAsync("JSON.stringify({height:innerHeight,width:innerWidth,html:document.body.innerHTML.slice(0,1000),rect:document.images[0]?.getBoundingClientRect().toJSON()})");
            await WaitUntil(()=>requests.Contains("A/image-00.png"),TimeSpan.FromSeconds(5));
            if(await core.ExecuteScriptAsync("document.body.innerText.includes('Demand reader')")!="true"||release.Task.IsCompleted)throw new InvalidOperationException("Document waited for image decoding.");
            if(requests.Any(path=>path!="A/image-00.png"))throw new InvalidOperationException("Offscreen media or unplayed video was read eagerly.");
            report["bodyReadyWhileImageDecodeBlocked"]=true;report["offscreenMediaAndVideoNotRead"]=true;
            release.TrySetResult();
            async Task WaitScript(string script)
            {for(int n=0;n<100;n++){if(await core.ExecuteScriptAsync(script)=="true")return;await Task.Delay(50);}throw new InvalidOperationException("Browser condition failed: "+script);}
            await WaitScript("document.images[0].naturalWidth>0");
            await core.ExecuteScriptAsync("document.querySelector('video').scrollIntoView()");await Task.Delay(100);
            await core.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');v.muted=true;v.play();})()");
            await WaitScript("document.querySelector('video').currentTime>0.1");
            if(!requests.Contains("clip.mp4"))throw new InvalidOperationException("Playback bypassed local resource approval.");
            report["localVideoActuallyPlayed"]=true;
            await core.ExecuteScriptAsync("document.querySelector('video').currentTime=6");
            await WaitScript("document.querySelector('video').currentTime>=6&&!document.querySelector('video').seeking");
            report["videoSeekAcrossBoundedRanges"]=new FileInfo(video).Length>LocalMediaRange.MaximumBytes&&requests.Count(path=>path=="clip.mp4")>1;
            if(!(bool)report["videoSeekAcrossBoundedRanges"])throw new InvalidOperationException("Video fixture did not exercise multiple bounded reads.");
            await core.ExecuteScriptAsync("document.images[1].scrollIntoView()");
            await WaitScript("document.images[1].naturalWidth>0");
            await WaitScript("document.querySelector('video').paused");
            if(core.Settings.IsScriptEnabled||core.Settings.IsWebMessageEnabled||requests.Any(path=>path.StartsWith("http",StringComparison.Ordinal)))throw new InvalidOperationException("Markdown security boundary changed.");
            report["scrollLoadsImageAndPausesHiddenVideo"]=true;
            long plain=(await catalog.FindOrdinal(resultHandle.Id,"plain.txt"))!.Value;
            await SelectBrowserOrdinal(results!,checked((int)plain),lifetime.Token);
            await WaitUntil(()=>markdownResourceRequests==0,TimeSpan.FromSeconds(10));
            if(TextScroll.Visibility!=Visibility.Visible||MarkdownHost.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Leaving media did not restore plain text.");
            if(Environment.GetCommandLineArgs().Contains("--verify-markdown-reopen"))
            {
                var repeated=new List<object>();report["repeatedDocumentLoads"]=repeated;
                for(int i=0;i<20;i++)
                {
                    if(i%4==0)await DisposeMarkdownView();
                    var watch=System.Diagnostics.Stopwatch.StartNew();
                    await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
                    report["webviewEvents"]=webviewEvents.ToArray();report["quality"]=QualityLabel.Text;
                    if(markdown?.CoreWebView2 is not {} repeatedCore||MarkdownHost.Visibility!=Visibility.Visible||
                        await repeatedCore.ExecuteScriptAsync("document.body.innerText.includes('Demand reader')")!="true")
                        throw new InvalidOperationException($"Markdown reopen {i} did not display.");
                    repeated.Add(new{iteration=i,recreatedView=i%4==0,elapsedMs=watch.Elapsed.TotalMilliseconds});
                    await SelectBrowserOrdinal(results!,checked((int)plain),lifetime.Token);
                    await WaitUntil(()=>markdownResourceRequests==0,TimeSpan.FromSeconds(10));
                }
            }
            report["leavingDocumentRetiresResourceWork"]=true;report["status"]="PASS";
        }
        finally{release.TrySetResult();verifyMarkdownResourceBarrier=null;}
    }
}
