using FolderLens.Core;
using FolderLens.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace FolderLens.UnitTests;

public class ScanSnapshotPressureTests(ITestOutputHelper output)
{
    private sealed record PressureEvent(double Milliseconds,string Stage,long WalBytes,int Packets,bool Pressure,string? ScanState,string? SnapshotState);
    [Fact]
    public async Task LargeWalWithoutActiveSnapshotDoesNotPauseScan()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-pressure",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(directory,"source");Directory.CreateDirectory(source);
        for(int i=0;i<3;i++)await File.WriteAllTextAsync(Path.Combine(source,$"file-{i}.txt"),"fixture");
        string data=Path.Combine(directory,"catalog");
        await using var catalog=new CatalogStore(data,new SnapshotLimits{CatalogWalBytes=8L<<20});
        await catalog.Initialize();long epoch=await catalog.OpenRoot("scan",source);
        await catalog.CheckpointCatalog(true);
        // A separate capacity-style reader pins the physical WAL while no
        // browsing snapshot owns pressure. This must not suspend enumeration.
        using var capacity=DatabaseExecutor.Open(Path.Combine(data,"catalog.sqlite"));
        using var held=capacity.BeginTransaction(deferred:true);
        using(var command=capacity.CreateCommand())
        {command.Transaction=held;command.CommandText="SELECT count(*) FROM Roots";Assert.Equal(1L,command.ExecuteScalar());}
        await catalog.Write(c=>
        {
            using var command=c.CreateCommand();
            command.CommandText="CREATE TABLE IdleWalFixture(payload BLOB); INSERT INTO IdleWalFixture VALUES(zeroblob(7340032));";
            return command.ExecuteNonQuery();
        });
        string wal=Path.Combine(data,"catalog.sqlite-wal");long before=new FileInfo(wal).Length;
        Assert.True(before>=6L<<20,"The negative fixture did not retain a high-water WAL.");
        Assert.False(catalog.SnapshotUnderPressure);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var progress=await new DirectoryIndexer(catalog).Scan("scan",source,epoch,true,[],null,timeout.Token);
        Assert.Equal("ready",progress.State);Assert.Equal(3,progress.Files);
        Assert.False(catalog.SnapshotUnderPressure);
        long after=new FileInfo(wal).Length;Assert.True(after>=6L<<20);
        output.WriteLine(JsonSerializer.Serialize(new{stage="idle-high-wal",before,after,pressure=catalog.SnapshotUnderPressure,progress.Files,progress.State}));
    }
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task ScanYieldsToPressuredSnapshotAndRemainsCancellable(bool cancelScan,bool walPressure)
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-pressure",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(directory,"source");Directory.CreateDirectory(source);
        int count=walPressure?4000:520;
        for(int i=0;i<count;i++)await File.WriteAllTextAsync(Path.Combine(source,$"file-{i:D8}-"+new string('a',120)+".txt"),"fixture");
        await using var catalog=new CatalogStore(Path.Combine(directory,"catalog"),new SnapshotLimits{SoftDeadline=walPressure?TimeSpan.FromSeconds(15):TimeSpan.Zero,CatalogWalBytes=8L<<20});
        await catalog.Initialize();await catalog.SeedBenchmark(4000);long epoch=await catalog.OpenRoot("scan",source);
        await catalog.CheckpointCatalog(true);
        using var held=new ManualResetEventSlim();using var entered=new ManualResetEventSlim();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await catalog.Read(c=>
        {
            SQLitePCL.raw.sqlite3_progress_handler(c.Handle,1000,_=>
            {entered.Set();held.Wait(timeout.Token);return 0;},null);return true;
        });
        Task<ResultHandle>? snapshot=null;Task<ScanProgress>? scan=null;
        using var scanStop=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        int packets=0;
        string wal=Path.Combine(directory,"catalog","catalog.sqlite-wal");
        var clock=Stopwatch.StartNew();var timeline=new ConcurrentQueue<PressureEvent>();
        Exception? primaryFailure=null;
        void Capture(string stage)
        {
            var file=new FileInfo(wal);long bytes=file.Exists?file.Length:0;
            timeline.Enqueue(new(clock.Elapsed.TotalMilliseconds,stage,bytes,Volatile.Read(ref packets),catalog.SnapshotUnderPressure,scan?.Status.ToString(),snapshot?.Status.ToString()));
        }
        try
        {
            Capture("before-snapshot");
            snapshot=catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=["image","video"],Grouping=new(true)},1,1,timeout.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Capture("snapshot-reader-held");
            if(!walPressure)while(!catalog.SnapshotUnderPressure)await Task.Delay(5,timeout.Token);
            var indexer=new DirectoryIndexer(catalog){PacketReceived=(_,packet)=>{Interlocked.Increment(ref packets);Capture("packet-"+packet.State+"-entries-"+packet.Entries.Length);}};
            Capture("before-scan");
            scan=indexer.Scan("scan",source,epoch,true,[],null,scanStop.Token);
            if(walPressure)
            {
                var watch=Stopwatch.StartNew();long observedBytes=-1;
                while(!catalog.SnapshotUnderPressure&&watch.Elapsed<TimeSpan.FromSeconds(10))
                {
                    var file=new FileInfo(wal);long bytes=file.Exists?file.Length:0;
                    if(bytes!=observedBytes){observedBytes=bytes;Capture("waiting-pressure");}
                    await Task.Delay(5,timeout.Token);
                }
                Capture("pressure-observed");
                Assert.True(catalog.SnapshotUnderPressure,"Scan writes did not reach the WAL high-water mark.");
            }
            await Task.Delay(250,timeout.Token);
            Capture("first-paused-check");
            if(!walPressure)Assert.Equal(0,Volatile.Read(ref packets));
            else
            {
                long bytes=new FileInfo(wal).Length;int pausedPackets=Volatile.Read(ref packets);
                Assert.InRange(bytes,6L<<20,(8L<<20)-1);
                await Task.Delay(250,timeout.Token);
                Capture("second-paused-check");
                Assert.Equal(bytes,new FileInfo(wal).Length);Assert.Equal(pausedPackets,Volatile.Read(ref packets));
            }
            Assert.False(scan.IsCompleted,"A scan must yield while the snapshot is holding a pressured read transaction.");
            if(cancelScan)
            {
                Capture("before-scan-cancel");
                scanStop.Cancel();
                Assert.Equal("cancelled",(await scan.WaitAsync(TimeSpan.FromSeconds(2))).State);
            }
            Capture("before-reader-release");
            held.Set();Assert.Equal(4000,(await snapshot).Count);
            Capture("snapshot-completed");
            Assert.False(catalog.SnapshotUnderPressure);
            if(!cancelScan)Assert.Equal(count,(await scan).Files);
            Capture("scan-completed");
        }
        catch(Exception error){primaryFailure=error;Capture("primary-failure");throw;}
        finally
        {
            var cleanupFailures=new List<Exception>();
            held.Set();scanStop.Cancel();
            if(scan is not null)try{await scan;}catch(OperationCanceledException){}catch(Exception error){cleanupFailures.Add(error);}
            if(snapshot is not null)try{await snapshot;}catch(OperationCanceledException){}catch(Exception error){cleanupFailures.Add(error);}
            try{await catalog.Read(c=>{SQLitePCL.raw.sqlite3_progress_handler(c.Handle,0,null,null);return true;});}
            catch(Exception error){cleanupFailures.Add(error);}
            Capture("cleanup-completed");
            // A failed snapshot can also fail when joined for cleanup. Preserve
            // the original assertion/exception rather than replacing its stack.
            output.WriteLine("Pressure fixture: "+directory);
            output.WriteLine("Primary failure: "+(primaryFailure?.ToString()??"none"));
            foreach(var error in cleanupFailures)output.WriteLine("Cleanup failure: "+error);
            foreach(var entry in timeline)output.WriteLine(JsonSerializer.Serialize(entry));
            if(primaryFailure is null&&cleanupFailures.Count>0)throw new AggregateException("Snapshot pressure cleanup failed.",cleanupFailures);
        }
    }
}
