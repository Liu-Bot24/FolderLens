using System.Diagnostics;
using System.Reflection;
using FolderLens.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FolderLens.UnitTests;

[CollectionDefinition("Native resource budget",DisableParallelization=true)]
public sealed class NativeResourceBudgetCollection { }

[Collection("Native resource budget")]
public sealed class ResourceSamplingTests(ITestOutputHelper output)
{
    [Fact]
    public void NewWorkerAdmissionPreservesPreviouslyMeasuredHostReserve()
    {
        var gate=typeof(WorkerJob).GetField("aggregateSync",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        lock(gate)
        {
            long before=WorkerJob.AggregateLimit;
            try
            {
                WorkerJob.SetAggregateLimit(512L<<20);
                WorkerJob.EnsureAggregateLimit();
                Assert.Equal(512L<<20,WorkerJob.AggregateLimit);
                WorkerJob.SetAggregateLimit(8L<<30,allowIncrease:false);
                Assert.Equal(512L<<20,WorkerJob.AggregateLimit);
                WorkerJob.SetAggregateLimit(256L<<20,allowIncrease:false);
                Assert.Equal(256L<<20,WorkerJob.AggregateLimit);
            }
            finally{WorkerJob.SetAggregateLimit(before>0?before:1L<<30);}
        }
    }

    [Fact]
    public void NativeMemoryQueryMeasuresPrivateCommitWithoutFullProcessRefresh()
    {
        using var process=Process.GetCurrentProcess();
        process.Refresh();long reference=process.PrivateMemorySize64;
        long measured=WorkerResources.ReadPrivateBytes(process);
        Assert.InRange(measured,reference/2,reference*2);
        var watch=Stopwatch.StartNew();
        for(int i=0;i<30;i++){process.Refresh();_ = process.PrivateMemorySize64;}
        double previous=watch.Elapsed.TotalMilliseconds;watch.Restart();
        for(int i=0;i<30;i++)Assert.True(WorkerResources.ReadPrivateBytes(process)>0);
        output.WriteLine($"30 samples: Process.Refresh={previous:F2} ms, native={watch.Elapsed.TotalMilliseconds:F2} ms");
    }
}
