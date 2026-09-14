using System.Runtime.InteropServices;
using FolderLens.Core;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

internal static class WindowFocus
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private static readonly bool verification=Environment.GetCommandLineArgs().Contains("--verify-refresh");
    internal static long Foreground=>GetForegroundWindow().ToInt64();
    internal static bool IsForeground(Window window)=>Foreground==WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64();
    internal static bool MayActivate(Window window,long requestedForeground)=>!verification&&WindowActivationPolicy.CanActivate(requestedForeground,Foreground,WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64());
    internal static void Show(Window window,long requestedForeground)
    {
        if(MayActivate(window,requestedForeground))window.Activate();else window.AppWindow.Show(false);
    }
    internal static bool Focus(Window window,UIElement element,FocusState state)=>!verification&&IsForeground(window)&&element.Focus(state);
}
