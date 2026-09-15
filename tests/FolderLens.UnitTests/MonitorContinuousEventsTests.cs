using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MonitorContinuousEventsTests
{
    [Fact]
    public async Task ContinuousChangesPublishBeforeTheProducerStops()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-events",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        int notifications=0;using var monitor=new RootChangeMonitor(directory,()=>Interlocked.Increment(ref notifications));
        var started=System.Diagnostics.Stopwatch.StartNew();
        while(started.Elapsed<TimeSpan.FromSeconds(3)){monitor.MarkDirty();await Task.Delay(40);}
        Assert.True(Volatile.Read(ref notifications)>0,"持续事件期间没有提交任何更新。");
    }
}
