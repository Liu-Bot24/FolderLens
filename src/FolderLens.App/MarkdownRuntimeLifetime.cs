using Microsoft.Web.WebView2.Core;
using System.Diagnostics;

namespace FolderLens.App;

/// <summary>Owns one exclusive browser process group until native shutdown finishes.</summary>
internal sealed class MarkdownRuntimeLifetime
{
    private readonly CoreWebView2Environment environment;
    private readonly Action<string> record;
    private TaskCompletionSource changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> exited=[];
    private readonly Dictionary<int,Process> browsers=[];
    private readonly DateTime created=DateTime.UtcNow;
    private Exception? observationFailure;
    private bool completed;
    private bool exitSubscribed,processesSubscribed;
    public int RecoveredProcessCount {get;private set;}

    public MarkdownRuntimeLifetime(CoreWebView2Environment environment,string ownedFolder,Action<string> record)
    {
        // The caller creates a unique session directory and explicitly requests
        // ExclusiveUserDataFolderAccess. Never adopt an environment redirected
        // to a user's browser profile by a runtime override.
        if(!Path.GetFullPath(environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(ownedFolder).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Markdown 运行环境未使用当前会话的数据目录。");
        this.environment=environment;this.record=record;
        try
        {
            environment.BrowserProcessExited+=BrowserExited;exitSubscribed=true;
            environment.ProcessInfosChanged+=ProcessInfosChanged;processesSubscribed=true;
        }
        catch{DisposeTracking();throw;}
        CaptureBrowsers();
    }

    private void BrowserExited(CoreWebView2Environment sender,CoreWebView2BrowserProcessExitedEventArgs args)
    {
        exited.Add(checked((int)args.BrowserProcessId));NotifyChanged();
    }

    private void ProcessInfosChanged(CoreWebView2Environment sender,object args)
    {
        CaptureBrowsers();NotifyChanged();
    }

    public void CaptureBrowsers()
    {
        if(completed)return;
        try{ReadBrowsers();}
        catch(Exception error){observationFailure=error;record("MarkdownRuntime process observation failed: "+error.GetType().Name);}
    }
    private void ReadBrowsers()
    {
        foreach(var info in environment.GetProcessInfos())
        {
            if(info.Kind!=CoreWebView2ProcessKind.Browser||browsers.ContainsKey(info.ProcessId))continue;
            Process? process=null;
            try
            {
                process=Process.GetProcessById(info.ProcessId);
                // Retain the opened OS process handle, not a PID that can later
                // refer to an unrelated process. Only the exclusive environment
                // supplies these IDs; no process-name-wide termination is used.
                _=process.Handle;
                if(process.HasExited){process.Dispose();continue;}
                if(process.StartTime.ToUniversalTime()<created||!OwnedBrowserProcess(process)||
                    !string.Equals(Path.GetFileName(process.MainModule?.FileName),"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("无法确认 Markdown 显示进程的归属。");
                browsers.Add(process.Id,process);process=null;
            }
            catch(ArgumentException){/* The reported process exited before its handle was opened. */}
            finally{process?.Dispose();}
        }
    }

    public async Task Retire(Task? initialization)
    {
        Exception? failure=null;
        try
        {
            if(initialization is not null)
            {
                try{await initialization.WaitAsync(TimeSpan.FromSeconds(5));}
                catch(Exception)when(initialization.IsCompleted){/* Close aborts a pending native controller. */}
            }
            CaptureBrowsers();
            ThrowIfObservationFailed();
            // Close is asynchronous in the runtime. Starting another controller
            // against its profile before this event races browser shutdown.
            try{await WaitForExit(TimeSpan.FromSeconds(2));}
            catch(TimeoutException)
            {
                CaptureBrowsers();
                ThrowIfObservationFailed();
                foreach(var browser in browsers.Values)
                {
                    if(browser.HasExited)continue;
                    record($"MarkdownRuntime shutdown timed out; retiring owned browser pid={browser.Id}");
                    browser.Kill();RecoveredProcessCount++;
                }
                await Task.WhenAll(browsers.Values.Select(browser=>browser.WaitForExitAsync()))
                    .WaitAsync(TimeSpan.FromSeconds(3));
                // This event also accounts for crashpad, which is deliberately
                // absent from GetProcessInfos. Do not reuse or remove the UDF
                // while any part of the previous browser group still owns it.
                await WaitForExit(TimeSpan.FromSeconds(3));
            }
            record($"MarkdownRuntime retired; recoveredProcesses={RecoveredProcessCount}");
        }
        catch(Exception error){failure=error;}
        finally{var cleanupFailure=DisposeTracking();failure??=cleanupFailure;}
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void NotifyChanged(){var previous=changed;changed=new(TaskCreationOptions.RunContinuationsAsynchronously);previous.TrySetResult();}
    private void ThrowIfObservationFailed()
    {if(observationFailure is not null)throw new IOException("无法确认 Markdown 进程已安全退出。",observationFailure);}
    private async Task WaitForExit(TimeSpan budget)
    {
        var watch=Stopwatch.StartNew();
        while(true)
        {
            CaptureBrowsers();ThrowIfObservationFailed();
            if(browsers.All(pair=>pair.Value.HasExited&&exited.Contains(pair.Key)))return;
            var remaining=budget-watch.Elapsed;if(remaining<=TimeSpan.Zero)throw new TimeoutException();
            await changed.Task.WaitAsync(remaining);
        }
    }
    private Exception? DisposeTracking()
    {
        completed=true;Exception? failure=null;
        void DisposeOne(Action action){try{action();}catch(Exception error){failure??=error;}}
        if(processesSubscribed)DisposeOne(()=>environment.ProcessInfosChanged-=ProcessInfosChanged);
        if(exitSubscribed)DisposeOne(()=>environment.BrowserProcessExited-=BrowserExited);
        foreach(var browser in browsers.Values)DisposeOne(browser.Dispose);
        return failure;
    }

    private static bool OwnedBrowserProcess(Process process)
    {
        int status=NtQueryInformationProcess(process.Handle,0,out var info,System.Runtime.InteropServices.Marshal.SizeOf<BasicInformation>(),out _);
        return status==0&&info.ParentProcessId==Environment.ProcessId;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BasicInformation {public nint ExitStatus,Peb,Affinity,Priority,ProcessId,ParentProcessId;}
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process,int informationClass,out BasicInformation info,int length,out int returnedLength);
}
