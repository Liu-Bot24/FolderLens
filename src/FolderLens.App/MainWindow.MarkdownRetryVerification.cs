using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyMarkdownRetries(string source,Dictionary<string,object> report)
    {
        string[] names=Enumerable.Range(0,18).Select(i=>$"retry-{i:D2}.png").ToArray();
        foreach(string name in names.Append("denied.png").Append("timeout.png").Append("exhausted.png"))File.Copy(Path.Combine(source,"A","image-00.png"),Path.Combine(source,name));
        await File.WriteAllTextAsync(Path.Combine(source,"retry.md"),"# Retry\n\n"+string.Join(" ",names.Append("denied.png").Append("timeout.png").Append("exhausted.png").Select(name=>$"![image]({name})")));
        await File.WriteAllTextAsync(Path.Combine(source,"leave.txt"),"leave");
        suppressFilters=true;SelectTag(Category,"text");suppressFilters=false;
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts=new Dictionary<string,int>();
        verifyMarkdownResourceBarrier=async(path,token)=>
        {
            attempts[path]=attempts.GetValueOrDefault(path)+1;
            if(path=="denied.png")throw new UnauthorizedAccessException("Controlled permanent denial");
            if(path=="exhausted.png")throw new TimeoutException("Controlled repeated timeout");
            if(path=="timeout.png"&&attempts[path]==1)await Task.Delay(Timeout.Infinite,token);
            await release.Task.WaitAsync(token);
        };
        try
        {
            long ordinal=(await catalog!.FindOrdinal(resultHandle!.Id,"retry.md"))!.Value;
            await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
            var core=markdown?.CoreWebView2??throw new InvalidOperationException("Markdown view missing.");
            // Host-only layout keeps all twenty elements in view without user input.
            await core.ExecuteScriptAsync("document.querySelectorAll('img').forEach(n=>{n.style.width='16px';n.style.height='16px';n.style.display='inline-block'})");
            await WaitUntil(()=>markdownResources.Values.Any(resource=>resource.Failures>0),TimeSpan.FromSeconds(5));
            report["queueSaturationObserved"]=markdownResources.Values.Any(resource=>resource.Failures>0&&!resource.PermanentFailure);
            if(!(bool)report["queueSaturationObserved"])throw new InvalidOperationException("Did not reach bounded admission.");
            release.TrySetResult();
            for(int n=0;n<500;n++)
            {
                if(await core.ExecuteScriptAsync("Array.from(document.images).filter(n=>n.naturalWidth>0).length") == "19")break;
                await Task.Delay(50);
            }
            if(await core.ExecuteScriptAsync("Array.from(document.images).filter(n=>n.naturalWidth>0).length")!="19")throw new InvalidOperationException("Transient images did not recover.");
            await Task.Delay(1500);
            if(attempts.GetValueOrDefault("denied.png")!=1||attempts.GetValueOrDefault("timeout.png")!=2||attempts.GetValueOrDefault("exhausted.png") is <1 or >4||!markdownResources.Values.Single(r=>r.RelativeUrl=="exhausted.png").PermanentFailure)throw new InvalidOperationException("Permanent denial or retry bound failed.");
            report["attempts"]=attempts;report["timeoutAndSaturationRecovered"]=true;report["permanentDenialNotRetried"]=true;report["retryBudgetExhausted"]=true;
            long plain=(await catalog.FindOrdinal(resultHandle.Id,"leave.txt"))!.Value;
            await SelectBrowserOrdinal(results!,checked((int)plain),lifetime.Token);
            await WaitUntil(()=>markdownResourceRequests==0,TimeSpan.FromSeconds(5));
            foreach(bool pending in new[]{true,false})
            {
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int requests=0;
                verifyMarkdownResourceBarrier=async(path,token)=>
                {
                    if(path!=names[0])return;requests++;entered.TrySetResult();
                    if(pending)await finish.Task; // Deliberately finish after navigation.
                    else throw new TimeoutException("Controlled backoff before navigation");
                };
                try
                {
                    await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if(!pending)await WaitUntil(()=>markdownResources.Values.Any(r=>r.RelativeUrl==names[0]&&r.Failures==1&&r.RetryAfter>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),TimeSpan.FromSeconds(2));
                    await SelectBrowserOrdinal(results!,checked((int)plain),lifetime.Token);finish.TrySetResult();
                    await WaitUntil(()=>markdownResourceRequests==0,TimeSpan.FromSeconds(5));await Task.Delay(1500);
                    if(requests!=1||MarkdownHost.Visibility!=Visibility.Collapsed||core.Settings.IsScriptEnabled||core.Settings.IsWebMessageEnabled)throw new InvalidOperationException("Navigation did not retire pending resource/backoff.");
                }
                finally{finish.TrySetResult();}
            }
            report["navigationRetiresPendingAndBackoff"]=true;report["status"]="PASS";
        }
        finally{release.TrySetResult();verifyMarkdownResourceBarrier=null;}
    }
}
