using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MediaNavigationTests
{
    [Theory]
    [InlineData(0,1,3)]
    [InlineData(0,2,5)]
    [InlineData(0,20,5)]
    [InlineData(5,-1,3)]
    [InlineData(5,-2,0)]
    [InlineData(0,-1,0)]
    [InlineData(5,1,5)]
    public async Task WheelSequenceSkipsNonMediaAndPreservesFrozenOrder(int origin,int steps,int expected)
    {
        string[] kinds=["image","text","markdown","video","audio","image","other"];
        Assert.Equal(expected,PreviewSequence.Move(kinds,origin,steps));
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-navigation",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(kinds.Length);
        await catalog.Write(c=>{for(int i=0;i<kinds.Length;i++){using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET kind=$kind WHERE entry_id=$id";cmd.Parameters.AddWithValue("$kind",kinds[i]);cmd.Parameters.AddWithValue("$id",(i+1).ToString("D12"));cmd.ExecuteNonQuery();}return true;});
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET kind='text'";return cmd.ExecuteNonQuery();});
        Assert.Equal(expected,await catalog.ReadMediaNavigationTarget(handle.Id,origin,steps));
    }
    [Fact] public void NoMediaNeighborKeepsCurrentImage()=>Assert.Equal(0,PreviewSequence.Move(["image","text","audio","other"],0,10));
}
