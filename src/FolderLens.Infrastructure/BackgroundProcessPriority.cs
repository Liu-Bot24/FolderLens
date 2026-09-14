using System.ComponentModel;
using System.Diagnostics;

namespace FolderLens.Infrastructure;

internal static class BackgroundProcessPriority
{
    public static void Apply(Process process)
    {
        try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}
        // Only an observed exit is exempt. A live-process failure still propagates.
        catch(InvalidOperationException) when(process.HasExited){}
        catch(Win32Exception) when(process.HasExited){}
    }
}
