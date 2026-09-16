using FolderLens.Infrastructure;
using FolderLens.Contracts;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class LocalMediaRangeTests
{
    [Fact]public async Task ActualWorkerReadsBoundedBytesAndRejectsReplacedVideo()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-media-range",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"source.mp4");byte[] bytes=Enumerable.Range(0,600000).Select(i=>(byte)(i%251)).ToArray();await File.WriteAllBytesAsync(path,bytes);
        var observation=FileReadObservation.Read(path);var stamp=new SourceFileStamp(observation.Length,observation.ModifiedUtcTicks,observation.Signature);
        string exe=Path.Combine(project!.FullName,"src","FolderLens.Content.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Content.Worker.exe");
        await using var worker=new WorkerClient(exe,Path.Combine(directory,"worker"));var context=new RequestContext("root",1,1,1,1,1);
        var first=await worker.ReadMediaRange(path,"bytes=100-",context,stamp,CancellationToken.None);
        Assert.Equal(bytes.AsSpan(100,LocalMediaRange.MaximumBytes).ToArray(),first.Message.Metadata!.Value.GetProperty("bytes").GetBytesFromBase64());
        string replacement=Path.Combine(directory,"replacement.mp4");await File.WriteAllBytesAsync(replacement,bytes);File.SetLastWriteTimeUtc(replacement,new DateTime(stamp.ModifiedUtcTicks,DateTimeKind.Utc));
        File.Move(path,Path.Combine(directory,"before.mp4"));File.Move(replacement,path);
        Assert.Equal("FileChanged",(await Assert.ThrowsAsync<IOException>(()=>worker.ReadMediaRange(path,"bytes=0-1",context,stamp,CancellationToken.None))).Message);
        observation=FileReadObservation.Read(path);stamp=new(observation.Length,observation.ModifiedUtcTicks,observation.Signature);
        var last=await worker.ReadMediaRange(path,"bytes=-2",context,stamp,CancellationToken.None);
        Assert.Equal(bytes[^2..],last.Message.Metadata!.Value.GetProperty("bytes").GetBytesFromBase64());
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker.ReadMediaRange(path,null,context,stamp,stop.Token));
    }
    [Theory]
    [InlineData(null,1000,0,1000)]
    [InlineData("bytes=100-199",1000,100,100)]
    [InlineData("bytes=-100",1000,900,100)]
    [InlineData("bytes=900-",1000,900,100)]
    [InlineData("bytes=0-",1000000,0,LocalMediaRange.MaximumBytes)]
    public void BoundedRanges(string? range,long length,long start,int count)=>Assert.Equal((start,count),LocalMediaRange.Parse(range,length));
    [Theory]
    [InlineData("bytes=1000-")][InlineData("bytes=9-1")][InlineData("bytes=-0")][InlineData("bytes=0-1,3-4")][InlineData("bytes=+1-2")][InlineData("bytes=9223372036854775808-")]
    public void InvalidRanges(string range)=>Assert.Throws<InvalidDataException>(()=>LocalMediaRange.Parse(range,1000));
    [Theory][InlineData("clip.mp4",true)][InlineData("clip%2Emp4",true)][InlineData("clip.exe",false)][InlineData("clip.m3u8",false)]
    public void OnlySupportedContainers(string path,bool expected)=>Assert.Equal(expected,LocalMediaRange.IsVideo(path));
}
