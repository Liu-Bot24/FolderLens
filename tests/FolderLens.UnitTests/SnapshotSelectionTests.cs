using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class SnapshotSelectionTests
{
    [Fact]
    public async Task ReorderMapsCompleteSelectionAndAnIndependentPreview()
    {
        await using var store=await Fixture(10);var filter=new FilterSpec{RootId="benchmark",Kinds=[]};
        var before=await store.CreateSnapshot(filter,1,1);Assert.True(await store.RetainSnapshot(before.Id));
        var after=await store.CreateSnapshot(filter with{Sort=new("name","desc")},1,2);
        var mapped=await store.MapSnapshotSelection(before,after,[new(0,2),new(6,1)],4);
        Assert.Equal(new[]{new OrdinalRange(3,1),new OrdinalRange(8,2)},mapped.Ranges);
        Assert.Equal(5,mapped.PreviewOrdinal);
        var old=await store.ReadPage(before.Id,0);var next=await store.ReadPage(after.Id,0);
        var expected=new[]{old[0].EntryId,old[1].EntryId,old[6].EntryId}.Order().ToArray();
        Assert.Equal(expected,next.Where(item=>mapped.Ranges.Any(range=>item.Ordinal>=range.Start&&item.Ordinal<range.End)).Select(item=>item.EntryId).Order());
        await store.ReleaseSnapshot(before.Id);
    }
    [Fact]
    public async Task RemovedFileDropsOnlyThatIdentityAndVersionChangesKeepTheRemainingFileSelected()
    {
        await using var store=await Fixture(10);var filter=new FilterSpec{RootId="benchmark",Kinds=[]};
        var before=await store.CreateSnapshot(filter,1,1);Assert.True(await store.RetainSnapshot(before.Id));
        var rows=await store.ReadPage(before.Id,0);
        await store.Write(c=>
        {
            using var q=c.CreateCommand();q.CommandText="UPDATE Files SET entry_state='missing' WHERE entry_id=$removed; UPDATE Files SET file_version=file_version+1 WHERE entry_id=$changed; UPDATE SchemaInfo SET catalog_revision=catalog_revision+1";
            q.Parameters.AddWithValue("$removed",rows[3].EntryId);q.Parameters.AddWithValue("$changed",rows[4].EntryId);return q.ExecuteNonQuery();
        });
        var after=await store.CreateSnapshot(filter,1,2);var mapped=await store.MapSnapshotSelection(before,after,[new(2,3)],3);
        Assert.Equal(new OrdinalRange(2,2),Assert.Single(mapped.Ranges));Assert.Null(mapped.PreviewOrdinal);
        await store.ReleaseSnapshot(before.Id);
    }
    [Fact]
    public async Task LargeSelectionStaysACompressedRangeAndCancelledOrInvalidRequestsDoNotMap()
    {
        await using var store=await Fixture(50000);var filter=new FilterSpec{RootId="benchmark",Kinds=[]};
        var before=await store.CreateSnapshot(filter,1,1);Assert.True(await store.RetainSnapshot(before.Id));
        var after=await store.CreateSnapshot(filter with{Sort=new("name","desc")},1,2);
        var mapped=await store.MapSnapshotSelection(before,after,[new(0,before.Count)],40000);
        Assert.Equal(new OrdinalRange(0,before.Count),Assert.Single(mapped.Ranges));Assert.Equal(9999,mapped.PreviewOrdinal);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.MapSnapshotSelection(before,after,[new(-1,1)],null));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.MapSnapshotSelection(before,after with{RootId="other"},[],null));
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.MapSnapshotSelection(before,after,[new(0,1)],null,stop.Token));
        await store.ReleaseSnapshot(before.Id);
    }
    private static async Task<CatalogStore> Fixture(int count)
    {
        var store=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-selection-map",Guid.NewGuid().ToString("N")));
        await store.Initialize();await store.SeedBenchmark(count);return store;
    }
}
