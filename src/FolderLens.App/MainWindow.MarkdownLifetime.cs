using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim markdownLoadGate=new(1,1);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? markdownReleaseTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? markdownViewportTimer;
    private bool markdownLoading;
    private MarkdownRuntimeLifetime? markdownRuntime;
    private Task markdownRuntimeRetirement=Task.CompletedTask;
    private MarkdownRuntimeLifetime? retiringMarkdownRuntime;
    private Task? retiringMarkdownInitialization;
    private Task? markdownControllerInitialization;
    private async Task AwaitMarkdownRetirement(CancellationToken token)
    {
        // Retry proof of shutdown, never discard a failed task and reuse its
        // profile blindly. The retained owner still holds original OS handles.
        if(markdownRuntimeRetirement.IsFaulted&&retiringMarkdownRuntime is {} runtime)
            markdownRuntimeRetirement=RetireMarkdownRuntime(runtime,retiringMarkdownInitialization,null);
        await markdownRuntimeRetirement.WaitAsync(token);
        retiringMarkdownRuntime=null;retiringMarkdownInitialization=null;
    }
    private async Task<CoreWebView2Environment> CreateMarkdownEnvironment(CancellationToken token)
    {
        await AwaitMarkdownRetirement(token);
        string fixedRuntime=Path.Combine(AppContext.BaseDirectory,"runtime","webview2");
        string folder=Path.Combine(RuntimeDataDirectory,"webview");
        var options=new CoreWebView2EnvironmentOptions{ExclusiveUserDataFolderAccess=true};
        var environment=await CoreWebView2Environment.CreateWithOptionsAsync(Directory.Exists(fixedRuntime)?fixedRuntime:null,folder,options)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5),token);
        markdownRuntime=new MarkdownRuntimeLifetime(environment,folder,RecordWebView);
        return environment;
    }
    private CancellationTokenSource? markdownPresentationStop;
    private bool MarkdownPresentationRequested=>markdownPresentationStop is {IsCancellationRequested:false};
    private void CancelMarkdownPresentation()
    {
        var previous=markdownPresentationStop;markdownPresentationStop=null;
        if(previous is null)return;
        // Leaving the rendered view retires only its work. The original text,
        // line index and search session remain usable without being reopened.
        previous.Cancel();previous.Dispose();navigationComplete?.TrySetCanceled();
        markdownViewportTimer?.Stop();
    }
    private void InitializeMarkdownLifetime()
    {
        markdownViewportTimer=DispatcherQueue.CreateTimer();markdownViewportTimer.Interval=TimeSpan.FromMilliseconds(200);
        markdownViewportTimer.Tick+=async(_,_)=>await UpdateMarkdownViewport();
        markdownReleaseTimer=DispatcherQueue.CreateTimer();markdownReleaseTimer.Interval=TimeSpan.FromSeconds(60);markdownReleaseTimer.IsRepeating=false;
        markdownReleaseTimer.Tick+=(_,_)=>
        {
            if(!closing&&!markdownLoading&&MarkdownHost.Visibility!=Visibility.Visible)ReleaseMarkdownView();
        };
        MarkdownHost.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,(_,_)=>
        {
            if(MarkdownHost.Visibility!=Visibility.Visible){CancelMarkdownPresentation();_=PauseMarkdownMedia();}
            ScheduleMarkdownRelease();
        });
    }
    private void ScheduleMarkdownRelease()
    {
        if(markdownReleaseTimer is null)return;
        if(!closing&&!markdownLoading&&MarkdownPresentationRequested&&MarkdownHost.Visibility==Visibility.Visible)markdownViewportTimer?.Start();else markdownViewportTimer?.Stop();
        if(closing||markdownLoading||markdown is null||MarkdownHost.Visibility==Visibility.Visible)markdownReleaseTimer.Stop();
        else if(!markdownReleaseTimer.IsRunning)markdownReleaseTimer.Start();
    }
    private void ConfigureMarkdownView(WebView2 view,CoreWebView2Environment environment)
    {
        var core=view.CoreWebView2;
        core.Settings.IsScriptEnabled=false;core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;core.Settings.AreDevToolsEnabled=false;
        core.NavigationCompleted+=(_,e)=>{if(!ReferenceEquals(markdown,view))return;RecordWebView($"NavigationCompleted success={e.IsSuccess} error={e.WebErrorStatus}");if(e.NavigationId==markdownNavigationId)navigationComplete?.TrySetResult(e.IsSuccess);};
        core.ProcessFailed+=(_,e)=>
        {
            if(!ReferenceEquals(markdown,view)||closing)return;
            RecordWebView($"ProcessFailed {e.ProcessFailedKind}");navigationComplete?.TrySetException(new IOException("Markdown 显示进程失败。"));
            if(!markdownLoading)DispatcherQueue.TryEnqueue(()=>{if(ReferenceEquals(markdown,view)&&!closing){bool visible=MarkdownHost.Visibility==Visibility.Visible;CancelMarkdownPresentation();ReleaseMarkdownView();if(visible){MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;QualityLabel.Text="Markdown 显示失败，已保留原文。可再次切换排版重试。";}}});
        };
        core.NewWindowRequested+=(_,e)=>e.Handled=true;core.DownloadStarting+=(_,e)=>e.Cancel=true;core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.NavigationStarting+=(_,e)=>{if(!ReferenceEquals(markdown,view)||closing||!MarkdownPresentationRequested){e.Cancel=true;return;}RecordWebView($"NavigationStarting user={e.IsUserInitiated}");if(markdownDocumentUrl.Length>0&&e.Uri.Split('#')[0]==markdownDocumentUrl){markdownNavigationId=e.NavigationId;return;}e.Cancel=true;if(e.IsUserInitiated&&Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http" && uri.Host!="folderlens.local")Process.Start(new ProcessStartInfo(e.Uri){UseShellExecute=true});};
        core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested+=async(_,e)=>await ServeMarkdownResource(view,environment,e);
    }
    private void ReleaseMarkdownView()
    {
        markdownReleaseTimer?.Stop();markdownViewportTimer?.Stop();var previous=markdown;markdown=null;
        navigationComplete?.TrySetCanceled();navigationComplete=null;markdownDocument=[];ResetMarkdownResources();markdownDocumentUrl="";
        MarkdownHost.Content=null;
        markdownRuntime?.CaptureBrowsers();
        Exception? closeFailure=null;
        try{previous?.Close();}catch(Exception error){closeFailure=error;}
        if(markdownRuntime is {} runtime)
        {
            markdownRuntime=null;
            var initialization=markdownControllerInitialization;markdownControllerInitialization=null;
            retiringMarkdownRuntime=runtime;retiringMarkdownInitialization=initialization;
            markdownRuntimeRetirement=RetireMarkdownRuntime(runtime,initialization,closeFailure);
        }
        else if(closeFailure is not null)markdownRuntimeRetirement=Task.FromException(closeFailure);
        if(previous is not null)RecordWebView("Markdown view released");
    }
    private async Task RetireMarkdownRuntime(MarkdownRuntimeLifetime runtime,Task? initialization,Exception? closeFailure)
    {
        try{await runtime.Retire(initialization);if(closeFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();}
        catch(Exception error){RecordWebView("MarkdownRuntime retirement failed: "+error.GetType().Name);throw;}
    }
    private async Task DisposeMarkdownView()
    {
        CancelMarkdownPresentation();markdownReleaseTimer?.Stop();await markdownLoadGate.WaitAsync();
        try{ReleaseMarkdownView();await AwaitMarkdownRetirement(CancellationToken.None);}finally{markdownLoadGate.Release();}
    }
}
