using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CapacityServiceTests
{
    [Fact]
    public async Task LiveCapacityFollowsReopenedRootWhileSnapshotRemainsFrozen()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-capacity",Guid.NewGuid().ToString("N")),root=Path.Combine(fixture,"source");
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"one.jpg"),"one");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();
        long first=await catalog.OpenRoot("root",root);var scan=new DirectoryIndexer(catalog);await scan.Scan("root",root,first,true,[],null,CancellationToken.None);
        var service=new CapacityService(catalog);var frozen=await catalog.CreateSnapshot(new(){RootId="root",ObservedRootEpoch=first},first,1);await catalog.RetainSnapshot(frozen.Id);
        var before=await service.EntireRoot("root",CancellationToken.None,currentObservationsOnly:true);
        long second=await catalog.OpenRoot("root",root);File.WriteAllText(Path.Combine(root,"two.jpg"),"two");await scan.Scan("root",root,second,true,[],null,CancellationToken.None);
        var after=await service.EntireRoot("root",CancellationToken.None,currentObservationsOnly:true);
        Assert.Equal(1,before.Rows.Single(row=>row.RelativePath=="").SubtreeFiles);
        Assert.Equal(2,after.Rows.Single(row=>row.RelativePath=="").SubtreeFiles);
        Assert.Equal(1,(await service.Result(frozen,CancellationToken.None)).Rows.Single(row=>row.RelativePath=="").SubtreeFiles);
        await catalog.ReleaseSnapshot(frozen.Id);
    }
    [Fact]
    public async Task DirectoryAggregateMatchesFileStreamIncludingUnknownAllocationAndMissingRows()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-capacity",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(200);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET allocated_bytes=CASE WHEN entry_id LIKE '%1' THEN NULL ELSE logical_bytes+100 END; UPDATE Files SET entry_state='missing' WHERE entry_id LIKE '%2'";return cmd.ExecuteNonQuery();});
        var expected=await catalog.Read(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT relative_path,logical_bytes,allocated_bytes FROM Files WHERE root_id='benchmark' AND entry_state='present'";
            using var reader=cmd.ExecuteReader();var files=new List<CapacityFile>();while(reader.Read())files.Add(new(reader.GetString(0),reader.GetInt64(1),reader.IsDBNull(2)?null:reader.GetInt64(2)));
            return Capacity.Build(files).Single(r=>r.RelativePath=="");
        });
        var report=await new CapacityService(catalog).EntireRoot("benchmark",CancellationToken.None);
        Assert.Equal(expected,report.Rows.Single(r=>r.RelativePath==""));Assert.True(expected.AllocationUnknown>0);Assert.Equal(180,expected.SubtreeFiles);
    }
    [Fact]
    public async Task ScanTerminationChangesLiveStampWithoutChangingFrozenResult()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-capacity",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(4);
        var service=new CapacityService(catalog);var before=await service.ChangeStamp("benchmark",CancellationToken.None);
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Roots SET scan_state='cancelled'; UPDATE SchemaInfo SET catalog_revision=catalog_revision+1";return cmd.ExecuteNonQuery();});
        var stamp=await service.ChangeStamp("benchmark",CancellationToken.None);Assert.Equal("cancelled",stamp.State);Assert.True(stamp.Revision>before.Revision);
        var partial=await service.EntireRoot("benchmark",CancellationToken.None);Assert.False(partial.IsComplete);Assert.Equal(stamp.Revision,partial.Revision);
        var frozen=await service.Result(handle,CancellationToken.None);Assert.True(frozen.IsComplete);Assert.Equal(4,frozen.Rows.Single(r=>r.RelativePath=="").SubtreeFiles);
    }
    [Fact]
    public async Task AggregateFailureStopsBoundedProducerAndReportsOverflow()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-capacity",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(1500);
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET logical_bytes=9223372036854775807 WHERE entry_id='000000000001'";return command.ExecuteNonQuery();});
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var task=new CapacityService(catalog).Result(handle,cancellation.Token);
        try{await Assert.ThrowsAsync<OverflowException>(()=>task.WaitAsync(TimeSpan.FromSeconds(2)));}
        finally{cancellation.Cancel();try{await task;}catch{}}
    }
    [Fact]
    public async Task SnapshotCapacityStaysFixedAfterLiveIndexChanges()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-capacity",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(4);
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        var service=new CapacityService(catalog);var before=await service.Result(handle,CancellationToken.None);
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET logical_bytes=1";return command.ExecuteNonQuery();});
        var frozen=await service.Result(handle,CancellationToken.None);var live=await service.EntireRoot("benchmark",CancellationToken.None);
        Assert.Equal(before.Rows,frozen.Rows);Assert.Equal(4,live.Rows.Single(r=>r.RelativePath=="").SubtreeLogical);
        Assert.NotEqual(live.Rows.Single(r=>r.RelativePath=="").SubtreeLogical,frozen.Rows.Single(r=>r.RelativePath=="").SubtreeLogical);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.Result(handle with{IsPendingView=true},CancellationToken.None));
    }
}
