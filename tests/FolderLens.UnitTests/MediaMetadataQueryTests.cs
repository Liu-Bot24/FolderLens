using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MediaMetadataQueryTests
{
    [Fact]
    public async Task ColdPlaylistMetadataIncludesFilteredOutMembersOnly()
    {
        string directory=Temp(),source=Path.Combine(directory,"source"),data=Path.Combine(directory,"data");Directory.CreateDirectory(source);
        string wave=Path.Combine(source,"sample.wav");
        using(var output=new BinaryWriter(File.Create(wave)))
        {
            output.Write("RIFF"u8);output.Write(36+96000);output.Write("WAVEfmt "u8);output.Write(16);output.Write((short)1);output.Write((short)1);output.Write(48000);output.Write(96000);output.Write((short)2);output.Write((short)16);output.Write("data"u8);output.Write(96000);output.Write(new byte[96000]);
        }
        string id;
        await using(var session=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await session.Catalog.OpenRoot("root",source);await new DirectoryIndexer(session.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            id=(await session.Catalog.CreateCollection("one second")).Id;await session.Catalog.ChangeCollectionItems([id],(await session.Catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items,true);
        }
        File.Copy(wave,Path.Combine(source,"not-saved.wav"));
        await using var current=await BrowsingSessionStorage.Open(data);await current.Catalog.RefreshPlaylist(id);
        var filter=new FilterSpec{RootId="collection:"+id,CollectionId=id,Kinds=["audio"],Ranges=new(){["durationMs"]=new(1000,1000)}};
        Assert.Empty((await current.Catalog.ReadFirstPage(filter)).Items);
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);string native=Path.Combine(project.FullName,"native","ffmpeg");
        await using var worker=new WorkerClient(Path.Combine(directory,"unused-image-worker.exe"),Path.Combine(directory,"temp"));
        string scanWorker=Path.Combine(project.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
        await new MetadataPump(current.Catalog,worker,new MediaTools(Path.Combine(native,"ffprobe.exe"),Path.Combine(native,"ffmpeg.exe")),scanWorker).FillAll(filter.RootId,filter.RootId,1,null,CancellationToken.None,id,observedOnly:true);
        Assert.Single((await current.Catalog.ReadFirstPage(filter)).Items);
        Assert.Empty((await current.Catalog.ReadFirstPage(filter with{Ranges=new(){["durationMs"]=new(2000,null)}})).Items);
        Assert.Equal(1,await current.Catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files";return (long)cmd.ExecuteScalar()!;}));
    }
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-metadata-tests",Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task MediaCasRejectsOldEpochAndVersionAndPreservesRationalAndNulls()
    {
        await using var catalog=new CatalogStore(Temp());await catalog.Initialize();await catalog.SeedBenchmark(1);
        const string id="000000000001";
        var metadata=new MediaMetadata(1234,1920,1080,"h264","aac",30000.0/1001,false,0){FormatId="mov",FrameRateNumerator=30000,FrameRateDenominator=1001};
        Assert.False(await catalog.ApplyMediaMetadata(id,2,"benchmark",1,metadata,"fixture"));
        Assert.False(await catalog.ApplyMediaMetadata(id,1,"benchmark",2,metadata,"fixture"));
        Assert.True(await catalog.ApplyMediaMetadata(id,1,"benchmark",1,metadata,"fixture"));
        var snapshot=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=["video"],VideoCodecs=["h264"],AudioCodecs=["aac"],FrameRate=new(29.97,29.98),Ranges=new(){["width"]=new(1920,1920),["durationMs"]=new(1234,1234)}},1,1);
        Assert.Equal(1,snapshot.Count);Assert.Equal(0,await catalog.FindOrdinal(snapshot.Id,"file1.jpg"));Assert.Null(await catalog.FindOrdinal(snapshot.Id,"missing.jpg"));
        Assert.False(await catalog.MarkMetadataFailure(id,1,"benchmark",2,["media"],"Timeout",false));
        Assert.True(await catalog.ApplyMediaMetadata(id,1,"benchmark",1,new(null,null,null,null,"aac",null,false,null){FormatId="aac"},"fixture"));
        var unknown=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=["audio"],Ranges=new(){["durationMs"]=new(0,0)}},1,2);
        Assert.Equal(0,unknown.Count);Assert.Equal(1,unknown.Pending);
    }
    [Fact]
    public async Task FillAllProbesRealPcmWaveWithoutImageWorkerAndDoesNotRewriteSource()
    {
        string directory=Temp();Directory.CreateDirectory(directory);string wave=Path.Combine(directory,"sample.wav");
        using(var output=new BinaryWriter(File.Create(wave)))
        {
            output.Write("RIFF"u8);output.Write(36+96000);output.Write("WAVEfmt "u8);output.Write(16);output.Write((short)1);output.Write((short)1);output.Write(48000);output.Write(96000);output.Write((short)2);output.Write((short)16);output.Write("data"u8);output.Write(96000);output.Write(new byte[96000]);
        }
        byte[] hash=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(wave));var stat=new FileInfo(wave);long modified=stat.LastWriteTimeUtc.Ticks;
        await using var catalog=new CatalogStore(Path.Combine(directory,"data"));await catalog.Initialize();await catalog.SeedBenchmark(1);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET name='sample.wav',relative_path='sample.wav',kind='audio',logical_bytes=$size,mtime_utc_ticks=$mtime,stat_signature=$signature";cmd.Parameters.AddWithValue("$size",stat.Length);cmd.Parameters.AddWithValue("$mtime",modified);cmd.Parameters.AddWithValue("$signature",FolderLens.Contracts.FileReadObservation.Read(wave).Signature);return cmd.ExecuteNonQuery();});
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);string native=Path.Combine(project.FullName,"native","ffmpeg");Assert.True(File.Exists(Path.Combine(native,"ffprobe.exe")),"The real packaged FFprobe fixture is required.");
        await using var worker=new WorkerClient(Path.Combine(directory,"unused-image-worker.exe"),Path.Combine(directory,"temp"));
        var pump=new MetadataPump(catalog,worker,new MediaTools(Path.Combine(native,"ffprobe.exe"),Path.Combine(native,"ffmpeg.exe")),Path.Combine(project.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe"));
        await pump.FillAll("benchmark",directory,1,null,CancellationToken.None);
        var snapshot=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=["audio"],Formats=["wav"],AudioCodecs=["pcm_s16le"],Ranges=new(){["durationMs"]=new(1000,1000)}},1,1);
        Assert.Equal(1,snapshot.Count);Assert.Equal(hash,System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(wave)));Assert.Equal(modified,File.GetLastWriteTimeUtc(wave).Ticks);
    }
}
