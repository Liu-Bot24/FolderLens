using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FolderLens.Infrastructure;

// Synchronous scope only: never hold across await or transfer to another thread.
internal sealed class BackgroundThreadScope : IDisposable
{
    public BackgroundThreadScope(){if(!SetThreadPriority(GetCurrentThread(),0x10000))throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void Dispose(){if(!SetThreadPriority(GetCurrentThread(),0x20000))throw new Win32Exception(Marshal.GetLastWin32Error());}
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetThreadPriority(IntPtr thread,int priority);
}
