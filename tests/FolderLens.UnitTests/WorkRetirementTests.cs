using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class WorkRetirementTests
{
    [Fact] public async Task ShutdownRejectsNewWorkAndWaitsForFinally()
    {
        var retirement=new WorkRetirement();var entered=new TaskCompletionSource();var finish=new TaskCompletionSource();var cleaned=false;
        async Task Producer()
        {
            using var work=retirement.Enter();Assert.NotNull(work);entered.SetResult();
            try{await finish.Task;}finally{await Task.Yield();cleaned=true;}
        }
        var active=Producer();await entered.Task;var drained=retirement.Stop();
        Assert.Null(retirement.Enter());Assert.False(drained.IsCompleted);Assert.False(cleaned);
        finish.SetResult();await drained;Assert.True(cleaned);await active;Assert.Null(retirement.Enter());
    }
    [Fact] public async Task NormalCompletionDoesNotStopOtherWork()
    {
        var retirement=new WorkRetirement();var first=retirement.Enter();first!.Dispose();first.Dispose();
        using(var next=retirement.Enter())Assert.NotNull(next);
        await retirement.Stop();Assert.Null(retirement.Enter());
    }
}
