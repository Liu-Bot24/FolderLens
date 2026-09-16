using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MediaCoverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundAudioWithoutCoverStillChecksSourceAndCancellation(bool cancel)
    {
        string native=Native(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-audio-version",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"audio.wav"),output=Path.Combine(directory,"cover.png");
        await BoundedProcess.Run(Path.Combine(native,"ffmpeg.exe"),["-nostdin","-v","error","-f","lavfi","-i","sine=frequency=440:duration=0.1",source],TimeSpan.FromSeconds(10),1<<20,CancellationToken.None);
        string project=Path.GetFullPath(Path.Combine(native,"..",".."));
        await using var probe=new SourceFileProbe(Path.Combine(project,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe"));
        string signature=FileObservationWriter.Signature(await probe.ReadObservation(source,CancellationToken.None));
        var media=new MediaTools(Path.Combine(native,"ffprobe.exe"),Path.Combine(native,"ffmpeg.exe"));
        using var stop=new CancellationTokenSource();
        media.VerificationBarrier=_=>{if(cancel)stop.Cancel();else File.SetLastWriteTimeUtc(source,DateTime.UtcNow.AddMinutes(1));return Task.CompletedTask;};
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>media.ReadCover(source,output,signature,probe,stop.Token,512,priority:WorkerPriority.Foreground));
        else Assert.Equal("FileChanged",(await Assert.ThrowsAsync<IOException>(()=>media.ReadCover(source,output,signature,probe,stop.Token,512,priority:WorkerPriority.Foreground))).Message);
        Assert.False(File.Exists(output));
    }
    private static string Native()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);return Path.Combine(project.FullName,"native","ffmpeg");
    }
    [Fact]
    public async Task EmptySuccessfulSeekFallsBackToFirstFrameWithoutChangingSource()
    {
        string native=Native(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-cover",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"短片.mp4"),cover=Path.Combine(directory,"cover.png");
        await BoundedProcess.Run(Path.Combine(native,"ffmpeg.exe"),["-nostdin","-v","error","-f","lavfi","-i","testsrc2=size=32x24:rate=2:duration=1","-c:v","mpeg4","-threads","1",source],TimeSpan.FromSeconds(10),1<<20,CancellationToken.None);
        byte[] before=System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source));
        var tools=new MediaTools(Path.Combine(native,"ffprobe.exe"),Path.Combine(native,"ffmpeg.exe"));var metadata=await tools.Probe(source,CancellationToken.None);
        // Stale duration / inaccurate seek can land beyond the last decodable frame.
        await tools.Cover(source,cover,metadata with{DurationMs=300000},CancellationToken.None);
        Assert.True(File.Exists(cover),"A zero-exit FFmpeg seek without an image must fall back, not report success.");
        Assert.True(new FileInfo(cover).Length>24);Assert.Equal(before,System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await Assert.ThrowsAsync<IOException>(()=>tools.Cover(source,source,metadata,CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(()=>tools.Cover(source,cover,metadata,CancellationToken.None));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>tools.Cover(source,Path.Combine(directory,"canceled.png"),metadata,canceled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(()=>tools.Cover(source,Path.Combine(directory,"failed.png"),metadata with{VideoStream=999},CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory,"failed.png")));Assert.Empty(Directory.GetFiles(directory,"*.tmp.png"));
    }
    [Fact]
    public async Task UsesEmbeddedAudioCoverBeforeAnyVideoSeek()
    {
        string native=Native(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-embedded-cover",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string ffmpeg=Path.Combine(native,"ffmpeg.exe"),poster=Path.Combine(directory,"poster.png"),source=Path.Combine(directory,"covered.m4a"),output=Path.Combine(directory,"cover.png");
        await BoundedProcess.Run(ffmpeg,["-nostdin","-v","error","-f","lavfi","-i","testsrc2=size=80x40:rate=1:duration=1","-frames:v","1","-c:v","png","-threads","1",poster],TimeSpan.FromSeconds(10),1<<20,CancellationToken.None);
        await BoundedProcess.Run(ffmpeg,["-nostdin","-v","error","-f","lavfi","-i","anullsrc=r=8000:cl=mono","-i",poster,"-t","1","-map","0:a","-map","1:v","-c:a","aac","-c:v","copy","-disposition:v","attached_pic",source],TimeSpan.FromSeconds(10),1<<20,CancellationToken.None);
        var tools=new MediaTools(Path.Combine(native,"ffprobe.exe"),ffmpeg);var metadata=await tools.Probe(source,CancellationToken.None);
        Assert.True(metadata.HasCover);Assert.NotNull(metadata.CoverStream);Assert.Null(metadata.VideoStream);
        await tools.Cover(source,output,metadata with{VideoStream=999,DurationMs=300000},CancellationToken.None,1024);
        byte[] header=await File.ReadAllBytesAsync(output);Assert.Equal(1024u,System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16,4)));Assert.Equal(512u,System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20,4)));
    }
    [Theory]
    [InlineData("16/15",512u,384u)]
    [InlineData("1/1",512u,410u)]
    public async Task CoverUsesDisplayAspectRatioForSquarePixelImage(string sar,uint width,uint height)
    {
        string native=Native(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-cover-aspect",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"clip.mp4"),output=Path.Combine(directory,"cover.png"),ffmpeg=Path.Combine(native,"ffmpeg.exe");
        await BoundedProcess.Run(ffmpeg,["-nostdin","-v","error","-f","lavfi","-i","testsrc2=size=720x576:rate=1:duration=1","-vf","setsar="+sar,"-c:v","mpeg4","-threads","1",source],TimeSpan.FromSeconds(10),1<<20,CancellationToken.None);
        var tools=new MediaTools(Path.Combine(native,"ffprobe.exe"),ffmpeg);
        await tools.Cover(source,output,await tools.Probe(source,CancellationToken.None),CancellationToken.None,512);
        byte[] png=await File.ReadAllBytesAsync(output);
        Assert.Equal(width,System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16,4)));
        Assert.Equal(height,System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20,4)));
    }
    [Fact]
    public async Task CoverLaneSerializesAndCancelsWithoutBlockingOtherWork()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held=await WorkerResources.Shared.Acquire(WorkerPriority.Visible,deadline.Token,WorkerLane.VideoCover);
        using var canceled=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pending=WorkerResources.Shared.Acquire(WorkerPriority.Foreground,canceled.Token,WorkerLane.VideoCover);
        Assert.False(pending.IsCompleted);
        using(var other=await WorkerResources.Shared.Acquire(WorkerPriority.Foreground,deadline.Token))Assert.Equal(WorkerLane.General,other.Lane);
        canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending);
        var next=WorkerResources.Shared.Acquire(WorkerPriority.Foreground,deadline.Token,WorkerLane.VideoCover);Assert.False(next.IsCompleted);
        held.Dispose();using var acquired=await next;Assert.Equal(WorkerLane.VideoCover,acquired.Lane);
    }
    [Theory]
    [InlineData("probe",false)]
    [InlineData("cover",false)]
    [InlineData("probe",true)]
    [InlineData("cover",true)]
    public async Task ChangedSourceCannotReturnMetadataOrCoverForPreviousVersion(string stage,bool foreground)
    {
        string native=Native(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-versioned-cover",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"clip.mp4"),output=Path.Combine(directory,"cover.png"),ffmpeg=Path.Combine(native,"ffmpeg.exe");
        await BoundedProcess.Run(ffmpeg,["-nostdin","-v","error","-f","lavfi","-i","testsrc2=size=32x24:rate=2:duration=1","-c:v","mpeg4","-threads","1",source],TimeSpan.FromSeconds(10),1<<20,CancellationToken.None);
        string project=Path.GetFullPath(Path.Combine(native,"..",".."));
        await using var probe=new SourceFileProbe(Path.Combine(project,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe"));
        string signature=FileObservationWriter.Signature(await probe.ReadObservation(source,CancellationToken.None));
        var media=new MediaTools(Path.Combine(native,"ffprobe.exe"),ffmpeg);
        var priority=foreground?WorkerPriority.Foreground:WorkerPriority.Visible;
        media.VerificationBarrier=at=>
        {
            if(at==stage)
            {
                // Same bytes and mtime at the same path, but a different file identity.
                string replacement=Path.Combine(directory,"replacement.mp4");File.Copy(source,replacement);
                File.SetLastWriteTimeUtc(replacement,File.GetLastWriteTimeUtc(source));File.Move(replacement,source,true);
            }
            return Task.CompletedTask;
        };
        var error=await Assert.ThrowsAsync<IOException>(()=>media.ReadCover(source,output,signature,probe,CancellationToken.None,512,priority:priority));
        Assert.Equal("FileChanged",error.Message);
        media.VerificationBarrier=null;
        signature=FileObservationWriter.Signature(await probe.ReadObservation(source,CancellationToken.None));
        var info=await media.ReadCover(source,Path.Combine(directory,"current.png"),signature,probe,CancellationToken.None,512,priority:priority);
        Assert.Equal(32,info.Width);Assert.True(File.Exists(Path.Combine(directory,"current.png")));
    }
}
