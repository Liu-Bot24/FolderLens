using System.Runtime.InteropServices;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

/// <summary>Pass an actual Shell item to Explorer, without command-line path parsing.</summary>
public static class ShellFileLocation
{
    // An unresponsive network namespace must not create an unbounded set of STAs.
    private static readonly SemaphoreSlim gate=new(1,1);

    public static Task Reveal(string path,CancellationToken cancellation=default)
        =>RevealCore(path,pidl=>SHOpenFolderAndSelectItems(pidl,0,0,0),cancellation);

    internal static async Task RevealCore(string path,Func<nint,int> open,CancellationToken cancellation=default)
    {
        path=PathRules.ValidateSource(path);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        var token=stop.Token;
        bool entered=false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);entered=true;
            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>
            {
                nint pidl=0;bool initialized=false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    Marshal.ThrowExceptionForHR(CoInitializeEx(0,2));initialized=true;
                    // ParseDisplayName can describe a missing item on some providers.
                    // Reject missing/inaccessible paths instead of opening a default folder.
                    File.GetAttributes(path);
                    token.ThrowIfCancellationRequested();
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(path,0,out pidl,0,out _));
                    if(pidl==0)throw new IOException("无法定位此文件。");
                    token.ThrowIfCancellationRequested();
                    // cidl=0 means: open the parent and select this fully qualified item.
                    Marshal.ThrowExceptionForHR(open(pidl));
                    done.TrySetResult();
                }
                catch(OperationCanceledException){done.TrySetCanceled(token);}
                catch(Exception ex){done.TrySetException(ex);}
                finally
                {
                    if(pidl!=0)Marshal.FreeCoTaskMem(pidl);
                    if(initialized)CoUninitialize();
                    gate.Release();
                }
            }){IsBackground=true,Name="FolderLens file location"};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();entered=false;
            // Observe a possible late native failure even if the caller has moved away.
            _=done.Task.ContinueWith(t=>_ = t.Exception,CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            await done.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
        {throw new TimeoutException("打开文件位置超时，请检查磁盘连接后重试。");}
        finally{if(entered)gate.Release();}
    }

    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string name,nint context,out nint pidl,uint attributes,out uint resultAttributes);
    [DllImport("shell32.dll")]private static extern int SHOpenFolderAndSelectItems(nint pidl,uint count,nint children,uint flags);
}
