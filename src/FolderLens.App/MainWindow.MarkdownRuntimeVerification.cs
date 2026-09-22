using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyMarkdownRuntimeLifetime(Dictionary<string,object> report)
    {
        string fixedRuntime=Path.Combine(AppContext.BaseDirectory,"runtime","webview2");
        if(Environment.GetCommandLineArgs().Contains("--verify-markdown-evergreen"))fixedRuntime="";
        string? runtimeFolder=Directory.Exists(fixedRuntime)?fixedRuntime:null;
        string sentinelFolder=Path.Combine(RuntimeDataDirectory,"sentinel-webview");
        string ownedFolder=Path.Combine(RuntimeDataDirectory,"runtime-verification-webview");
        var stages=new List<string>();var cycles=new List<object>();
        report["runtimeStages"]=stages;report["runtimeCycles"]=cycles;
        report["runtimeKind"]=runtimeFolder is null?"evergreen":"fixed";
        var panel=new Grid{Width=640,Height=240,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        panel.ColumnDefinitions.Add(new ColumnDefinition());panel.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRowSpan(panel,4);Shell.Children.Add(panel);Shell.UpdateLayout();
        CoreWebView2Environment? sentinelEnvironment=null;MarkdownRuntimeLifetime? sentinelOwner=null;
        WebView2? sentinel=null;Task? sentinelInitialization=null;Process? sentinelProcess=null;int sentinelPid=0;
        var sentinelExits=new HashSet<int>();Exception? primaryFailure=null;var finalizationFailures=new List<Exception>();

        void Record(string message)=>stages.Add(DateTimeOffset.UtcNow.ToString("O")+" "+message);
        void SentinelExited(CoreWebView2Environment sender,CoreWebView2BrowserProcessExitedEventArgs args)
            =>sentinelExits.Add(checked((int)args.BrowserProcessId));
        async Task<CoreWebView2Environment> EnvironmentFor(string folder)
        {
            var options=new CoreWebView2EnvironmentOptions{ExclusiveUserDataFolderAccess=true};
            return await CoreWebView2Environment.CreateWithOptionsAsync(runtimeFolder,folder,options).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token);
        }
        async Task ShowLocalDocument(WebView2 view)
        {
            var navigated=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(CoreWebView2 sender,CoreWebView2NavigationCompletedEventArgs args)=>navigated.TrySetResult(args.IsSuccess);
            view.CoreWebView2.NavigationCompleted+=Completed;
            try
            {
                view.CoreWebView2.NavigateToString("<!doctype html><html><body>Runtime lifetime fixture</body></html>");
                if(!await navigated.Task.WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token))throw new InvalidOperationException("运行环境测试文档导航失败。");
                if(await view.CoreWebView2.ExecuteScriptAsync("1+1").AsTask().WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token)!="2")
                    throw new InvalidOperationException("运行环境测试脚本未得到预期结果。");
            }
            finally{view.CoreWebView2.NavigationCompleted-=Completed;}
        }
        async Task AssertSentinel(string stage)
        {
            if(sentinelProcess is null||sentinelProcess.HasExited||sentinelExits.Count>0||sentinel?.CoreWebView2 is not {} core
                ||checked((int)core.BrowserProcessId)!=sentinelPid)
                throw new InvalidOperationException("独立哨兵浏览器被测试环境的清理误伤："+stage);
            string answer=await core.ExecuteScriptAsync("1+1").AsTask().WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token);
            if(answer!="2")throw new InvalidOperationException("哨兵浏览器在清理后不能执行脚本："+stage);
            Record($"sentinel-alive stage={stage} pid={sentinelPid} script={answer}");
        }
        async Task Cycle(string label,bool closeDuringInitialization,bool retryObservation=false)
        {
            CoreWebView2Environment? environment=null;MarkdownRuntimeLifetime? owner=null;
            WebView2? view=null;Task? initialization=null;Process? browser=null;int pid=0;
            bool closed=false,pendingAtClose=false,retired=false;Exception? failure=null;var cleanupFailures=new List<Exception>();
            bool failObservation=false;
            var exits=new HashSet<int>();
            void BrowserExited(CoreWebView2Environment sender,CoreWebView2BrowserProcessExitedEventArgs args)=>exits.Add(checked((int)args.BrowserProcessId));
            void CloseView(){if(view is null||closed)return;try{view.Close();}finally{closed=true;}}
            try
            {
                Record("create "+label);
                environment=await EnvironmentFor(ownedFolder);owner=new MarkdownRuntimeLifetime(environment,ownedFolder,Record,()=>{if(failObservation)throw new IOException("Injected process observation failure");});
                environment.BrowserProcessExited+=BrowserExited;
                view=new WebView2{Width=300,Height=220};Grid.SetColumn(view,1);panel.Children.Add(view);Shell.UpdateLayout();
                await WaitUntil(()=>view.IsLoaded,TimeSpan.FromSeconds(3));
                // Retire must receive this original native task, never a
                // timeout/cancellation wrapper that could finish before it.
                initialization=view.EnsureCoreWebView2Async(environment).AsTask();
                if(closeDuringInitialization)
                {
                    pendingAtClose=!initialization.IsCompleted;
                    CloseView();
                    if(!pendingAtClose)throw new InvalidOperationException("未覆盖初始化仍在进行时关闭控制器的路径。");
                }
                else
                {
                    await initialization.WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token);
                    pid=checked((int)view.CoreWebView2.BrowserProcessId);browser=Process.GetProcessById(pid);_=browser.Handle;
                    owner.CaptureBrowsers();await ShowLocalDocument(view);
                }
            }
            catch(Exception error){failure=error;}
            finally
            {
                try{CloseView();}catch(Exception error){cleanupFailures.Add(error);}
                if(owner is not null)
                {
                    if(retryObservation)
                    {
                        failObservation=true;
                        retiringMarkdownRuntime=owner;retiringMarkdownInitialization=initialization;
                        markdownRuntimeRetirement=RetireMarkdownRuntime(owner,initialization,null);
                        try{await markdownRuntimeRetirement;cleanupFailures.Add(new InvalidOperationException("Retirement accepted an unverified browser group."));}
                        catch(IOException){Record("unverified-retirement-rejected "+label);}
                        finally{failObservation=false;}
                    }
                    try{if(retryObservation)await AwaitMarkdownRetirement(lifetime.Token);else await owner.Retire(initialization);retired=true;}
                    catch(Exception error){cleanupFailures.Add(error);}
                }
                bool rawTerminal=initialization?.IsCompleted==true,processExited=false,exitEvent=pid==0||exits.Contains(pid);
                try
                {
                    processExited=browser is null||browser.HasExited;
                    if(failure is null&&cleanupFailures.Count==0)
                    {
                        if(!rawTerminal)failure=new InvalidOperationException("Retire 返回时原始初始化任务仍未结束："+label);
                        else if(!retired||!processExited||!exitEvent)failure=new InvalidOperationException("控制器关闭后缺少浏览器退出证据："+label);
                        else if(environment!.GetProcessInfos().Any(info=>info.Kind==CoreWebView2ProcessKind.Browser))
                            failure=new InvalidOperationException("退休后的运行环境仍有浏览器进程："+label);
                    }
                }
                catch(Exception error){cleanupFailures.Add(error);}
                cycles.Add(new{label,closeDuringInitialization,pendingAtClose,pid,rawTerminal,initializationStatus=initialization?.Status.ToString(),
                    retired,processExited,exitEvent,exitPids=exits.ToArray(),recoveredProcesses=owner?.RecoveredProcessCount,
                    error=failure?.ToString(),cleanupErrors=cleanupFailures.Select(error=>error.ToString()).ToArray()});
                if(environment is not null)environment.BrowserProcessExited-=BrowserExited;
                browser?.Dispose();if(view is not null)panel.Children.Remove(view);
            }
            if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
            if(cleanupFailures.Count>0)throw new AggregateException("运行环境验证清理失败："+label,cleanupFailures);
            await AssertSentinel(label);
        }
        try
        {
            sentinelEnvironment=await EnvironmentFor(sentinelFolder);
            sentinelOwner=new MarkdownRuntimeLifetime(sentinelEnvironment,sentinelFolder,Record);
            sentinelEnvironment.BrowserProcessExited+=SentinelExited;
            sentinel=new WebView2{Width=300,Height=220};panel.Children.Add(sentinel);Shell.UpdateLayout();
            await WaitUntil(()=>sentinel.IsLoaded,TimeSpan.FromSeconds(3));
            sentinelInitialization=sentinel.EnsureCoreWebView2Async(sentinelEnvironment).AsTask();
            await sentinelInitialization.WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token);
            sentinelPid=checked((int)sentinel.CoreWebView2.BrowserProcessId);sentinelProcess=Process.GetProcessById(sentinelPid);_=sentinelProcess.Handle;
            sentinelOwner.CaptureBrowsers();await ShowLocalDocument(sentinel);await AssertSentinel("initial");
            for(int i=0;i<3;i++)await Cycle(i==0?"initial-owned":"same-udf-recreation-"+i,false);
            await Cycle("close-pending-initialization",true);
            await Cycle("recovery-after-pending-close",false);
            await Cycle("recovery-after-observation-failure",false,true);
            await AssertSentinel("all-owned-retirements");
        }
        catch(Exception error){primaryFailure=error;}
        finally
        {
            try{sentinel?.Close();}catch(Exception error){finalizationFailures.Add(error);}
            if(sentinelOwner is not null)
            {
                try{await sentinelOwner.Retire(sentinelInitialization);}
                catch(Exception error){finalizationFailures.Add(error);}
            }
            bool exited=sentinelProcess is null||sentinelProcess.HasExited;
            bool exitEvent=sentinelPid==0||sentinelExits.Contains(sentinelPid);
            report["sentinelCleanup"]=new{sentinelPid,exited,exitEvent,rawTerminal=sentinelInitialization?.IsCompleted,
                recoveredProcesses=sentinelOwner?.RecoveredProcessCount,errors=finalizationFailures.Select(error=>error.ToString()).ToArray()};
            if(primaryFailure is null&&(!exited||!exitEvent))primaryFailure=new InvalidOperationException("哨兵运行环境自身未完成清理。");
            if(sentinelEnvironment is not null)sentinelEnvironment.BrowserProcessExited-=SentinelExited;
            sentinelProcess?.Dispose();Shell.Children.Remove(panel);
        }
        if(primaryFailure is not null)ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if(finalizationFailures.Count>0)throw new AggregateException("哨兵运行环境清理失败。",finalizationFailures);
        report["sameUserDataFolderRecreations"]=2;report["pendingInitializationRecovery"]=true;report["status"]="PASS";
    }
}
