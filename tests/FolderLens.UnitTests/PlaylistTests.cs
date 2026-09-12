using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PlaylistTests
{
    [Fact]
    public async Task SelectedRangesAreMergedAndStreamedInSnapshotOrderAcrossSegments()
    {
        string directory=Temp();await using var catalog=new CatalogStore(Path.Combine(directory,"data"));await catalog.Initialize();await catalog.SeedBenchmark(7);
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET kind='video' WHERE entry_id<>'000000000002'";return command.ExecuteNonQuery();});
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[],Grouping=new(true)},1,1);
        OrdinalRange[] selection=[new(5,2),new(1,3),new(0,2)];
        string output=Path.Combine(directory,"lists");
        var first=await MediaTools.Playlist(catalog,handle,directory,output,0,CancellationToken.None,2,selection);
        var second=await MediaTools.Playlist(catalog,handle,directory,output,first.NextOrdinal!.Value,CancellationToken.None,2,selection);
        var third=await MediaTools.Playlist(catalog,handle,directory,output,second.NextOrdinal!.Value,CancellationToken.None,2,selection);
        Assert.Equal(3,first.NextOrdinal);Assert.Equal(6,second.NextOrdinal);Assert.Null(third.NextOrdinal);
        var actual=new List<string>();foreach(var batch in new[]{first,second,third})actual.AddRange((await File.ReadAllLinesAsync(batch.Path)).Skip(2));
        Assert.Equal(new[]{1,3,4,6,7}.Select(index=>Path.Combine(directory,$"file{index}.jpg")),actual);
        Assert.DoesNotContain(Path.Combine(directory,"file5.jpg"),actual);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MediaTools.Playlist(catalog,handle,directory,output,0,CancellationToken.None,selection:[]));
    }
    [Fact]
    public void OrdinalSelectionKeepsLargeSelectionsCompactAndRejectsOutOfBounds()
    {
        Assert.Equal(new OrdinalRange(0,1_000_000),Assert.Single(OrdinalSelection.Normalize([new(500_000,500_000),new(0,500_001)],1_000_000)));
        Assert.Throws<ArgumentException>(()=>OrdinalSelection.Normalize([new(-1,1)],20));
        Assert.Throws<ArgumentException>(()=>OrdinalSelection.Normalize([new(19,2)],20));
        Assert.Throws<ArgumentException>(()=>OrdinalSelection.Normalize([new(long.MaxValue,1)],long.MaxValue));
        Assert.Empty(OrdinalSelection.Normalize([],0));
    }
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-playlists",Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task UsesSnapshotClassificationRatherThanExtensionOrLaterMetadata()
    {
        string directory=Temp();
        await using var catalog=new CatalogStore(Path.Combine(directory,"data"));await catalog.Initialize();await catalog.SeedBenchmark(3);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="""
            UPDATE Files SET kind='video',relative_path='actual-video.bin' WHERE entry_id='000000000001';
            UPDATE Files SET kind='audio',relative_path='audio-only.mp4' WHERE entry_id='000000000002';
            UPDATE Files SET kind='image',relative_path='cover.mp4' WHERE entry_id='000000000003';
            """;return cmd.ExecuteNonQuery();});
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1);
        // Later metadata must not silently replace the fixed session's media subsequence.
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET kind=CASE WHEN kind='video' THEN 'audio' ELSE 'video' END";return cmd.ExecuteNonQuery();});
        var batch=await MediaTools.Playlist(catalog,handle,directory,Path.Combine(directory,"lists"),0,CancellationToken.None);
        Assert.Equal(1,batch.Count);Assert.Equal(0,batch.FirstOrdinal);Assert.Equal(0,batch.LastOrdinal);
        Assert.Equal(new[]{Path.Combine(directory,"actual-video.bin")},(await File.ReadAllLinesAsync(batch.Path)).Skip(2));
    }
    [Fact]
    public async Task SegmentsFixedOrderWithoutDroppingVideosAndSupportsCurrentRange()
    {
        string directory=Temp(),root=Path.Combine(directory,"中文 源目录");
        await using var catalog=new CatalogStore(Path.Combine(directory,"data"));await catalog.Initialize();await catalog.SeedBenchmark(10004);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET relative_path=replace(relative_path,'.jpg','.mp4'),kind='video' WHERE entry_id<>'000000000002'";return cmd.ExecuteNonQuery();});
        var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1);
        string output=Path.Combine(directory,"lists");
        var first=await MediaTools.Playlist(catalog,handle,root,output,0,CancellationToken.None);
        Assert.Equal(10000,first.Count);Assert.Equal(0,first.FirstOrdinal);Assert.Equal(10000,first.LastOrdinal);Assert.Equal(10001,first.NextOrdinal);
        var second=await MediaTools.Playlist(catalog,handle,root,output,first.NextOrdinal!.Value,CancellationToken.None);
        Assert.Equal(3,second.Count);Assert.Null(second.NextOrdinal);
        var lines=(await File.ReadAllLinesAsync(first.Path)).Skip(2).Concat((await File.ReadAllLinesAsync(second.Path)).Skip(2)).ToArray();
        Assert.Equal(10003,lines.Distinct().Count());Assert.Equal(Path.Combine(root,"file1.mp4"),lines[0]);Assert.Equal(Path.Combine(root,"file10004.mp4"),lines[^1]);Assert.DoesNotContain(lines,line=>line.EndsWith(".jpg"));
        var current=await MediaTools.Playlist(catalog,handle,root,output,10002,CancellationToken.None);
        Assert.Equal(2,current.Count);Assert.Equal(Path.Combine(root,"file10003.mp4"),(await File.ReadAllLinesAsync(current.Path))[2]);
        Assert.Empty(Directory.GetFiles(output,"*.tmp"));
    }
    [Fact]
    public async Task RejectsUnsafeEmptyAndCancelledListsWithoutPublishingPartialFiles()
    {
        string directory=Temp(),output=Path.Combine(directory,"lists");
        await using var catalog=new CatalogStore(Path.Combine(directory,"data"));await catalog.Initialize();await catalog.SeedBenchmark(2);
        var empty=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MediaTools.Playlist(catalog,empty,directory,output,0,CancellationToken.None));
        Assert.Empty(Directory.GetFiles(output));
        foreach(string unsafePath in new[]{"..\\outside.mp4","bad\nname.mp4"})
        {
            await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET relative_path=$path,kind='video' WHERE entry_id='000000000002'; UPDATE Files SET relative_path='valid.mp4',kind='video' WHERE entry_id='000000000001'";cmd.Parameters.AddWithValue("$path",unsafePath);return cmd.ExecuteNonQuery();});
            var handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,2);
            await Assert.ThrowsAsync<InvalidDataException>(()=>MediaTools.Playlist(catalog,handle,directory,output,0,CancellationToken.None));
            Assert.Empty(Directory.GetFiles(output));
        }
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MediaTools.Playlist(catalog,empty,directory,output,0,cancel.Token));
        Assert.Empty(Directory.GetFiles(output));
    }
}
