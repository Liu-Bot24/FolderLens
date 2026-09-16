using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class WorkerTests
{
    private static string ProjectRoot()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"FolderLens.slnx")))directory=directory.Parent;
        return directory?.FullName??throw new DirectoryNotFoundException("Source tree required for media integration tests.");
    }
    private static string WorkerExecutable(string root)=>Environment.GetEnvironmentVariable("FOLDERLENS_TEST_MEDIA_WORKER")??Path.Combine(root,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
    [Fact] public async Task FrameRejectsOversizedAndUnknownFields()
    {
        using var oversized=new MemoryStream(new byte[]{1,0,16,0});await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerProtocol.Read(oversized));
        using var valid=new MemoryStream();await WorkerProtocol.Write(valid,new(){Type="hello",WorkerInstanceId="instance",Nonce=new string('a',32),BuildId=WorkerProtocol.BuildId});valid.Position=0;Assert.Equal("hello",(await WorkerProtocol.Read(valid)).Type);
        Assert.False(WorkerProtocol.SafeToken("../asset"));Assert.True(WorkerProtocol.SafeToken(new string('a',32)));
    }
    [Fact] public async Task ActualWorkerReturnsOwnImageAndRestartsAfterRawCancellation()
    {
        string root=ProjectRoot();string exe=WorkerExecutable(root);
        string jpeg=Path.Combine(root,"artifacts","m0","formats","synthetic.jpeg");string raw=Path.Combine(root,"fixtures","public","sony-a7r4a-14bit.ARW");
        Assert.True(File.Exists(exe));Assert.True(File.Exists(jpeg));Assert.True(File.Exists(raw));
        string cache=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using var worker=new WorkerClient(exe,cache);var context=new RequestContext("root",1,1,1,1,1);
        var first=await worker.Request(jpeg,"fit",context,new(256,256),CancellationToken.None);Assert.Equal("fit",first.Message.Quality);Assert.True(File.Exists(first.AssetPath));
        using var cancel=new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker.Request(raw,"rawDevelop",context with{SelectionGeneration=2},new(1600,1200),cancel.Token));
        var next=await worker.Request(jpeg,"fit",context with{SelectionGeneration=3},new(128,128),CancellationToken.None);
        Assert.NotEqual(first.Message.WorkerInstanceId,next.Message.WorkerInstanceId);Assert.Equal(3,next.Message.Context!.SelectionGeneration);Assert.True(File.Exists(next.AssetPath));
    }
    [Theory][InlineData("gif")][InlineData("webp")][InlineData("apng")]
    public async Task ActualAnimationFramesChangeAndStayInOneFile(string extension)
    {
        string root=ProjectRoot(),exe=WorkerExecutable(root);
        string input=Path.Combine(root,"artifacts","fixtures","animation","motion."+extension),temp=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using var worker=new WorkerClient(exe,temp);var context=new RequestContext("root",1,1,1,1,1);var first=await worker.Request(input,"animationOpen",context,new(128,128),CancellationToken.None);
        var next=await worker.Request(input,"animationFrame",context,new(128,128),CancellationToken.None);
        Assert.Equal(first.Message.WorkerInstanceId,next.Message.WorkerInstanceId);Assert.Equal(0,first.Message.Metadata!.Value.GetProperty("frameIndex").GetInt32());Assert.Equal(1,next.Message.Metadata!.Value.GetProperty("frameIndex").GetInt32());
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(first.AssetPath!))),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(next.AssetPath!))));
        Assert.InRange(next.Message.Metadata!.Value.GetProperty("durationMs").GetInt32(),150,250);
        await worker.Request(input,"animationClose",context,new(128,128),CancellationToken.None);
    }
    [Theory][InlineData("gif")][InlineData("webp")][InlineData("apng")]
    public async Task AnimationOriginalTileUsesRequestedFrameWithoutAdvancingPlayback(string extension)
    {
        string root=ProjectRoot(),input=Path.Combine(root,"artifacts","fixtures","animation","motion."+extension);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-animation-original",Guid.NewGuid().ToString("N"));
        await using var worker=new WorkerClient(WorkerExecutable(root),directory);var context=new RequestContext("root",1,1,1,1,1);
        var opened=await worker.Request(input,"animationOpen",context,new(16,16),CancellationToken.None);await worker.ReleaseAsset(opened);
        var first=await worker.Request(input,"fullTile",context,new(FrameIndex:0),CancellationToken.None);
        var second=await worker.Request(input,"fullTile",context,new(FrameIndex:1),CancellationToken.None);
        byte[] firstPng=await File.ReadAllBytesAsync(first.AssetPath!),secondPng=await File.ReadAllBytesAsync(second.AssetPath!);
        Assert.Equal("full",second.Message.Quality);Assert.Equal(1,second.Message.Metadata!.Value.GetProperty("frameIndex").GetInt32());
        Assert.Equal(first.Message.WorkerInstanceId,second.Message.WorkerInstanceId);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstPng)),Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(secondPng)));
        int width=second.Message.Metadata.Value.GetProperty("width").GetInt32(),height=second.Message.Metadata.Value.GetProperty("height").GetInt32();
        Assert.Equal(Math.Min(1024,width),System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(secondPng.AsSpan(16,4)));
        Assert.Equal(Math.Min(1024,height),System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(secondPng.AsSpan(20,4)));
        var next=await worker.Request(input,"animationFrame",context,new(16,16),CancellationToken.None);
        Assert.Equal(1,next.Message.Metadata!.Value.GetProperty("frameIndex").GetInt32());
        await worker.ReleaseAsset(first);await worker.ReleaseAsset(second);await worker.ReleaseAsset(next);
        await Assert.ThrowsAsync<InvalidDataException>(()=>worker.Request(input,"fullTile",context,new(FrameIndex:int.MaxValue),CancellationToken.None));
    }
    [Theory][InlineData("gif")][InlineData("webp")][InlineData("apng")]
    public async Task FiniteAnimationStopsAtItsTailAndReopensAtTheSavedCursor(string extension)
    {
        string root=ProjectRoot(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-animation-timing",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"finite."+extension);AnimationTimingFixture.FiniteCopy(Path.Combine(root,"artifacts","fixtures","animation","motion."+extension),input,extension);
        await using var worker=new WorkerClient(WorkerExecutable(root),Path.Combine(directory,"worker"));var context=new RequestContext("root",1,1,1,1,1);
        var first=await worker.Request(input,"animationOpen",context,new(64,64),CancellationToken.None);var data=first.Message.Metadata!.Value;int count=data.GetProperty("frameCount").GetInt32();Assert.True(count>=2);Assert.Equal(2,data.GetProperty("totalPlays").GetInt64());await worker.ReleaseAsset(first);
        for(int ordinal=1;ordinal<count*2;ordinal++)
        {
            var reply=await worker.Request(input,"animationFrame",context,new(64,64),CancellationToken.None);data=reply.Message.Metadata!.Value;
            Assert.Equal(ordinal%count,data.GetProperty("frameIndex").GetInt32());Assert.Equal((ordinal+1)/count,data.GetProperty("completedLoops").GetInt64());Assert.Equal(ordinal==count*2-1,data.GetProperty("completed").GetBoolean());
            if(extension=="apng"&&ordinal%count==count-1)Assert.Equal(777,data.GetProperty("durationMs").GetInt32());await worker.ReleaseAsset(reply);
        }
        await worker.Request(input,"animationClose",context,new(),CancellationToken.None);
        var lost=await Assert.ThrowsAsync<InvalidDataException>(()=>worker.Request(input,"animationFrame",context,new(),CancellationToken.None));Assert.Equal("AnimationSessionLost",lost.Message);
        var resumed=await worker.Request(input,"animationOpen",context,new(64,64,FrameIndex:1,CompletedLoops:1),CancellationToken.None);
        Assert.Equal(1,resumed.Message.Metadata!.Value.GetProperty("frameIndex").GetInt32());Assert.Equal(1+(count==2?1:0),resumed.Message.Metadata.Value.GetProperty("completedLoops").GetInt64());await worker.ReleaseAsset(resumed);
        await Assert.ThrowsAsync<InvalidDataException>(()=>worker.Request(input,"animationOpen",context,new(64,64,CompletedLoops:2),CancellationToken.None));
        var replay=await worker.Request(input,"animationOpen",context,new(64,64),CancellationToken.None);Assert.Equal(0,replay.Message.Metadata!.Value.GetProperty("frameIndex").GetInt32());await worker.ReleaseAsset(replay);
    }
    [Fact] public async Task RuntimeCapabilitiesUseNoSourceInputAndDoNotReplaceTheCurrentDecoder()
    {
        string root=ProjectRoot(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-capabilities",Guid.NewGuid().ToString("N"));
        await using var worker=new WorkerClient(WorkerExecutable(root),directory,WorkerPriority.Metadata);
        var report=await worker.GetRuntimeCapabilities(CancellationToken.None);
        Assert.Equal(WorkerProtocol.BuildId,report.BuildId);Assert.Equal(1,report.RawBridgeAbi);Assert.Equal("PASS",Assert.Single(report.Formats,f=>f.Format=="png").BasicDecodeStatus);
        Assert.Equal("NOT_RUN",Assert.Single(report.Formats,f=>f.Format=="arw").BasicDecodeStatus);Assert.All(report.Formats,f=>Assert.Equal("NOT_RUN",f.FullMatrixStatus));
        Assert.Empty(Directory.EnumerateFiles(directory,"*.input.json",SearchOption.AllDirectories));
        string input=Path.Combine(root,"artifacts","m0","formats","synthetic.jpeg");var context=new RequestContext("root",1,1,1,1,1);
        var before=await worker.Request(input,"fit",context,new(64,64),CancellationToken.None);await worker.ReleaseAsset(before);
        await worker.GetRuntimeCapabilities(CancellationToken.None);
        var after=await worker.Request(input,"fullTile",context,new(),CancellationToken.None);Assert.Equal(before.Message.WorkerInstanceId,after.Message.WorkerInstanceId);await worker.ReleaseAsset(after);
        await Assert.ThrowsAsync<InvalidDataException>(()=>worker.Request(input,"capabilities",context,new(),CancellationToken.None));
    }
    [Fact] public async Task IndexedSourceStampCannotBeReassignedToChangedImage()
    {
        string root=ProjectRoot(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-source-stamp",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"owned-copy.jpeg");File.Copy(Path.Combine(root,"artifacts","m0","formats","synthetic.jpeg"),input);
        var info=new FileInfo(input);var indexed=new SourceFileStamp(info.Length,info.LastWriteTimeUtc.Ticks);
        await using var worker=new WorkerClient(WorkerExecutable(root),Path.Combine(directory,"worker"),WorkerPriority.Prefetch);var context=new RequestContext("root",1,1,1,1,1);
        var before=await worker.Request(input,"fit",context,new(32,32),CancellationToken.None,indexed);await worker.ReleaseAsset(before);
        using(var changed=new FileStream(input,FileMode.Append,FileAccess.Write,FileShare.Read)){changed.WriteByte(0);}
        var stale=await Assert.ThrowsAsync<IOException>(()=>worker.Request(input,"fit",context,new(32,32),CancellationToken.None,indexed));Assert.Equal("FileChanged",stale.Message);
        info.Refresh();var updated=new SourceFileStamp(info.Length,info.LastWriteTimeUtc.Ticks);
        var after=await worker.Request(input,"fit",context with{FileVersion=2},new(32,32),CancellationToken.None,updated);Assert.Equal(2,after.Message.Context!.FileVersion);await worker.ReleaseAsset(after);
    }
    [Fact] public async Task UnindexedImageVersionIsReadInsideWorkerAndRefreshesOnChange()
    {
        string root=ProjectRoot(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-worker-stat",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"source.jpeg"),cache=Path.Combine(directory,"worker");
        File.Copy(Path.Combine(root,"artifacts","m0","formats","synthetic.jpeg"),input);
        await using var worker=new WorkerClient(WorkerExecutable(root),cache);var context=new RequestContext("root",1,1,1,1,1);
        var first=await worker.Request(input,"fit",context,new(32,32),CancellationToken.None);await worker.ReleaseAsset(first);
        string descriptor=Assert.Single(Directory.GetFiles(cache,"*.input.json",SearchOption.AllDirectories));
        using(var json=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(descriptor)))
        {
            Assert.False(json.RootElement.TryGetProperty("length",out _));
            Assert.False(json.RootElement.TryGetProperty("lastWriteTicks",out _));
        }
        using(var changed=new FileStream(input,FileMode.Append,FileAccess.Write,FileShare.Read)){changed.WriteByte(0);}
        var second=await worker.Request(input,"fit",context,new(32,32),CancellationToken.None);
        Assert.Equal(first.Message.WorkerInstanceId,second.Message.WorkerInstanceId);Assert.Equal("ok",second.Message.Status);await worker.ReleaseAsset(second);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>worker.Request(Path.Combine(directory,"absent.jpeg"),"fit",context,new(32,32),CancellationToken.None));
        var recovered=await worker.Request(input,"fit",context,new(32,32),CancellationToken.None);Assert.Equal("ok",recovered.Message.Status);await worker.ReleaseAsset(recovered);
    }
    [Fact] public async Task SameSizeAndTimestampReplacementCannotUseOldObservation()
    {
        string root=ProjectRoot(),directory=Path.Combine(Path.GetTempPath(),"FolderLens-worker-identity",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string input=Path.Combine(directory,"source.jpeg"),replacement=Path.Combine(directory,"replacement.jpeg");
        File.Copy(Path.Combine(root,"artifacts","m0","formats","synthetic.jpeg"),input);
        await using var probe=new SourceFileProbe(Path.Combine(root,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe"));
        var before=await probe.Read(input,CancellationToken.None);
        File.Copy(input,replacement);File.SetLastWriteTimeUtc(replacement,new DateTime(before.ModifiedUtcTicks,DateTimeKind.Utc));
        File.Move(input,Path.Combine(directory,"retained.jpeg"));File.Move(replacement,input);
        await using var worker=new WorkerClient(WorkerExecutable(root),Path.Combine(directory,"worker"));var context=new RequestContext("root",1,1,1,1,1);
        var error=await Assert.ThrowsAsync<IOException>(()=>worker.Request(input,"probe",context,new(ReadDetails:false),CancellationToken.None,before));
        Assert.Equal("FileChanged",error.Message);
        var current=await probe.Read(input,CancellationToken.None);Assert.NotEqual(before,current);
        var valid=await worker.Request(input,"probe",context,new(ReadDetails:false),CancellationToken.None,current);Assert.Equal("ok",valid.Message.Status);
    }
    [Fact]public async Task DisplayPixelsPreserveAlphaColorAndAllExifOrientations()
    {
        string exe=WorkerExecutable(ProjectRoot()),directory=Path.Combine(Path.GetTempPath(),"FolderLens-display-pixels",Guid.NewGuid().ToString("N"));
        string output=await BoundedProcess.Run(exe,["display-pixels",directory],TimeSpan.FromSeconds(15),1024*1024,CancellationToken.None);
        using(var report=System.Text.Json.JsonDocument.Parse(output))Assert.All(report.RootElement.GetProperty("checks").EnumerateArray(),check=>Assert.Equal("PASS",check.GetProperty("status").GetString()));
        await using var worker=new WorkerClient(exe,Path.Combine(directory,"worker"));var context=new RequestContext("root",1,1,1,1,1);
        foreach(string sample in new[]{"rgba8","rgba16"})
        {
            var reply=await worker.Request(Path.Combine(directory,sample+".png"),"fit",context,new(1,1),CancellationToken.None);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(directory,sample+"-fit.png")),await File.ReadAllBytesAsync(reply.AssetPath!));await worker.ReleaseAsset(reply);
        }
    }
    [Fact]public async Task ActualMultiVariantFramesAndRequestedTiffPageArePreserved()
    {
        string exe=WorkerExecutable(ProjectRoot()),directory=Path.Combine(Path.GetTempPath(),"FolderLens-image-variants",Guid.NewGuid().ToString("N"));
        string output=await BoundedProcess.Run(exe,["image-variants",directory],TimeSpan.FromSeconds(15),1024*1024,CancellationToken.None);
        using(var report=System.Text.Json.JsonDocument.Parse(output))Assert.All(report.RootElement.GetProperty("checks").EnumerateArray(),check=>Assert.True(check.GetProperty("Passed").GetBoolean()));
        string tiff=Path.Combine(directory,"profile-pages.tiff");await using var worker=new WorkerClient(exe,Path.Combine(directory,"worker"));var context=new RequestContext("root",1,1,1,1,1);
        // A newly started worker must honor PageIndex on fullTile without a prior page command.
        var second=await worker.Request(tiff,"fullTile",context,new(1024,1024,PageIndex:1),CancellationToken.None);
        Assert.Equal(64,second.Message.Metadata!.Value.GetProperty("width").GetInt32());Assert.Equal(32,second.Message.Metadata.Value.GetProperty("height").GetInt32());await worker.ReleaseAsset(second);
        var first=await worker.Request(tiff,"fullTile",context,new(1024,1024,PageIndex:0),CancellationToken.None);
        Assert.Equal(32,first.Message.Metadata!.Value.GetProperty("width").GetInt32());Assert.Equal(64,first.Message.Metadata.Value.GetProperty("height").GetInt32());await worker.ReleaseAsset(first);
    }
}
