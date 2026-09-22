using System.ComponentModel;
using System.Diagnostics;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class AuditA09RegressionTests
{
    [Fact]
    public void LegacyTextReplacementMustNotReuseIndexWithSameEntryVersion()
    {
        var old=new TextFileSnapshot(8196,123,null,null){SourceSignature="8196:123:456::legacy:AB:01:456:32"};
        var replaced=old with{SourceSignature="8196:123:456::legacy:AB:02:456:32"};
        string path=Path.Combine(Path.GetTempPath(),"same-entry.txt");
        Assert.NotEqual(old,replaced);
        Assert.NotEqual(old.Key(path,"utf-8",1),replaced.Key(path,"utf-8",1));
        Assert.Equal(old.Key(path,"utf-8",1),(old with{}).Key(path,"utf-8",1));
        Assert.NotEqual(old.Key(path,"utf-8",1),old.Key(path,"utf-8",2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedChildIsTerminatedWhenJobInitializationFails(bool assigning)
    {
        string executable=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
        Process? observed=null;var expected=new Win32Exception(8,"injected Job initialization failure");
        WorkerJob Create(Process child)
        {
            observed=Process.GetProcessById(child.Id);_=observed.Handle;
            if(!assigning)throw expected;
            return new WorkerJob(128L<<20);
        }
        try
        {
            var actual=await Assert.ThrowsAsync<Win32Exception>(()=>BoundedProcess.RunCore(executable,["-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 30"],TimeSpan.FromSeconds(10),4096,CancellationToken.None,WorkerPriority.Foreground,Create,(_,_)=>throw expected));
            Assert.Same(expected,actual);
            Assert.NotNull(observed);
            Assert.True(observed.HasExited,"The operation failed but its started child is still alive.");
        }
        finally
        {
            if(observed is not null){if(!observed.HasExited){observed.Kill(true);await observed.WaitForExitAsync();}observed.Dispose();}
        }
    }
}
