using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DemandAndFileOperationTests
{
    [Fact]
    public async Task SameSizeAndTimeReplacementMustNotBeRenamed()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"original.bin"),away=Path.Combine(folder,"away.bin");
        await File.WriteAllTextAsync(source,"first");var file=new FileInfo(source);
        var stamp=new SourceFileStamp(file.Length,file.LastWriteTimeUtc.Ticks);
        var request=new FileOperationRequest(source,stamp,FileOperationKind.Rename,Path.Combine(folder,"renamed.bin"),FileAllocation.InspectMetadata(source).PhysicalIdentity);
        File.Move(source,away);await File.WriteAllTextAsync(source,"other");File.SetLastWriteTimeUtc(source,new DateTime(stamp.ModifiedUtcTicks,DateTimeKind.Utc));
        await Assert.ThrowsAsync<IOException>(()=>FileOperations.Execute(request));
        Assert.Equal("other",await File.ReadAllTextAsync(source));Assert.True(File.Exists(away));
    }
    [Theory]
    [InlineData("sample.bin",false)]
    [InlineData("no-extension",true)]
    public async Task OrdinaryFileMutationCompletesDurableCleanupAfterCancellationAndRecoversFailure(string name,bool failCleanup)
    {
        string folder=Temp(),source=Path.Combine(folder,"source"),playlist=Path.Combine(folder,"saved.sqlite");Directory.CreateDirectory(source);
        string path=Path.Combine(source,name);await File.WriteAllTextAsync(path,"generated test file");
        string destination=Path.Combine(folder,"moved.bin");
        await using(var catalog=new CatalogStore(Path.Combine(folder,"runtime"),new(),playlist))
        {
            await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
            await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            var item=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items);
            var collection=await catalog.CreateCollection("generated");await catalog.ChangeCollectionItems([collection.Id],[item],true);
            var target=await catalog.ResolveFileOperation(item,captureLegacyLinks:true);
            using var lifetime=new CancellationTokenSource();
            await FileOperations.Execute(new(target.Path,target.Stamp,FileOperationKind.Move,destination,target.PhysicalIdentity),lifetime.Token);
            lifetime.Cancel(); // Actual disk action has succeeded; shutdown must not cancel completion.
            if(failCleanup)
            {
                await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="CREATE TEMP TRIGGER RejectCleanup BEFORE DELETE ON playlist.SavedLinks BEGIN SELECT RAISE(ABORT,'generated failure'); END";return q.ExecuteNonQuery();});
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>catalog.CompleteFileOperation(target));
                Assert.True(File.Exists(playlist+".completed-operation.json"));
            }
            else {await catalog.CompleteFileOperation(target);Assert.False((await catalog.ReadCollectionFlags([item]))[0]);}
        }
        Assert.False(File.Exists(path));Assert.Equal("generated test file",await File.ReadAllTextAsync(destination));
        await using(var restarted=new CatalogStore(Path.Combine(folder,"next-runtime"),new(),playlist)){await restarted.Initialize();}
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={playlist};Pooling=False");connection.Open();using var count=connection.CreateCommand();count.CommandText="SELECT count(*) FROM SavedLinks";Assert.Equal(0L,(long)count.ExecuteScalar()!);
        Assert.False(File.Exists(playlist+".completed-operation.json"));
    }
    [Fact]
    public async Task MutationRejectsRevisedPathAndEveryStaleObservation()
    {
        string folder=Temp();Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"sample.bin"),"generated");
        await using var catalog=new CatalogStore(Temp());await catalog.Initialize();long epoch=await catalog.OpenRoot("root",folder);
        await new DirectoryIndexer(catalog).Scan("root",folder,epoch,true,[],null,CancellationToken.None);
        var item=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items);
        await catalog.ResolveFileOperation(item);
        foreach(var stale in new[]{item with{PathRevision=item.PathRevision+1},item with{SourceRootEpoch=epoch+1},item with{BindingRevision=item.BindingRevision+1},item with{PhysicalIdentity="replacement"},item with{DirectoryLocationId="other"}})
            await Assert.ThrowsAsync<IOException>(()=>catalog.ResolveFileOperation(stale));
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE Files SET relative_path='renamed.bin',path_revision=path_revision+1";return q.ExecuteNonQuery();});
        await Assert.ThrowsAsync<IOException>(()=>catalog.ResolveFileOperation(item));
    }
    [Fact]
    public async Task FailedSettingSaveLeavesNoTemporaryFile()
    {
        string folder=Temp();Directory.CreateDirectory(Path.Combine(folder,"slideshow.json"));
        await Assert.ThrowsAsync<IOException>(()=>new AtomicSettings(folder).Save("slideshow.json",new{Seconds=10}));
        Assert.Empty(Directory.GetFiles(folder,"*.tmp"));
    }
    [Theory]
    [InlineData("name",false)] [InlineData("path",false)] [InlineData("logicalBytes",false)]
    [InlineData("modified",false)] [InlineData("width",true)] [InlineData("captured",true)] [InlineData("durationMs",true)]
    public void ContentWorkIsDemandedOnlyByContentQueries(string field,bool required)
    {
        Assert.Equal(required,MetadataDemand.ForQuery(new(){RootId="root",Sort=new(field)}));
        Assert.False(MetadataDemand.ForQuery(new(){RootId="root",NamePathQuery="holiday",Raw="exclude",FileExtensions=["jpg"],Ranges=new(){["logicalBytes"]=new(100,null)}}));
        Assert.True(MetadataDemand.ForQuery(new(){RootId="root",Ranges=new(){["height"]=new(100,null)}}));
    }
    [Fact]
    public async Task RawFilterIsImmediatelyUsableBeforeAnyContentProbe()
    {
        await using var catalog=new CatalogStore(Temp());await catalog.Initialize();await catalog.SeedBenchmark(2);
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE Files SET is_raw=NULL,extension=CASE WHEN entry_id='000000000001' THEN '.RAF' ELSE '.jpg' END";return q.ExecuteNonQuery();});
        var only=await catalog.ReadFirstPage(new(){RootId="benchmark",Raw="only"});var exclude=await catalog.ReadFirstPage(new(){RootId="benchmark",Raw="exclude"});
        Assert.Single(only.Items);Assert.Single(exclude.Items);Assert.NotEqual(only.Items[0].EntryId,exclude.Items[0].EntryId);
    }
    [Fact]
    public async Task ContentQueryProbesOneRequestedCandidateAndLeavesOtherNinetyNineUntouched()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"source");Directory.CreateDirectory(source);
        for(int i=0;i<100;i++)
        {
            using var output=new BinaryWriter(File.Create(Path.Combine(source,$"sample{i:D3}.wav")));
            output.Write("RIFF"u8);output.Write(36+9600);output.Write("WAVEfmt "u8);output.Write(16);output.Write((short)1);output.Write((short)1);output.Write(48000);output.Write(96000);output.Write((short)2);output.Write((short)16);output.Write("data"u8);output.Write(9600);output.Write(new byte[9600]);
        }
        await using var catalog=new CatalogStore(Path.Combine(folder,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
        string project=Project();string native=Path.Combine(project,"native","ffmpeg");
        await using var worker=new WorkerClient(Path.Combine(folder,"never-needed.exe"),Path.Combine(folder,"temp"));
        var pump=new MetadataPump(catalog,worker,new MediaTools(Path.Combine(native,"ffprobe.exe"),Path.Combine(native,"ffmpeg.exe")),Path.Combine(project,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe"));
        var filter=new FilterSpec{RootId="root",Kinds=["audio"],NamePathQuery="sample042",Ranges=new(){["durationMs"]=new(50,null)}};
        Assert.Empty((await catalog.ReadFirstPage(filter)).Items);
        await pump.FillAll("root",source,epoch,null,CancellationToken.None,filter:filter,readDetails:false);
        Assert.Single((await catalog.ReadFirstPage(filter)).Items);
        long inspected=await catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="SELECT count(DISTINCT entry_id) FROM FieldStates WHERE field_group='media'";return (long)q.ExecuteScalar()!;});
        Assert.Equal(1,inspected);
    }
    [Theory]
    [InlineData("../other.jpg")] [InlineData("CON.jpg")] [InlineData("new.jpg.")] [InlineData("new.jpg ")]
    public void RenameRejectsPathsAndReservedNames(string name)=>Assert.Throws<ArgumentException>(()=>FileOperations.RenameTarget(@"C:\photos\one.jpg",name));
    [Fact]
    public async Task RenameAndMovePreserveContentsAndRefuseCollisionsOrChangedSources()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"old.txt");await File.WriteAllTextAsync(source,"original");
        var stat=new FileInfo(source);var stamp=new SourceFileStamp(stat.Length,stat.LastWriteTimeUtc.Ticks);string renamed=FileOperations.RenameTarget(source,"renamed.txt");string identity=FileAllocation.InspectMetadata(source).PhysicalIdentity!;
        await FileOperations.Execute(new(source,stamp,FileOperationKind.Rename,renamed,identity));Assert.False(File.Exists(source));Assert.Equal("original",await File.ReadAllTextAsync(renamed));
        string targetFolder=Path.Combine(folder,"destination");Directory.CreateDirectory(targetFolder);string destination=Path.Combine(targetFolder,"renamed.txt");
        await FileOperations.Execute(new(renamed,stamp,FileOperationKind.Move,destination,identity));Assert.False(File.Exists(renamed));Assert.Equal("original",await File.ReadAllTextAsync(destination));
        await File.WriteAllTextAsync(renamed,"collision");
        await Assert.ThrowsAsync<IOException>(()=>FileOperations.Execute(new(destination,stamp,FileOperationKind.Move,renamed,identity)));Assert.Equal("collision",await File.ReadAllTextAsync(renamed));
        await File.WriteAllTextAsync(destination,"a different source");
        await Assert.ThrowsAsync<IOException>(()=>FileOperations.Execute(new(destination,stamp,FileOperationKind.Rename,Path.Combine(targetFolder,"bad.txt"),identity)));Assert.True(File.Exists(destination));
    }
    [Fact]
    public async Task SuccessfulOperationRetiresOldCollectionMappingWithoutFollowingNewName()
    {
        await using var catalog=new CatalogStore(Temp());await catalog.Initialize();await catalog.SeedBenchmark(2);
        var item=(await catalog.ReadFirstPage(new(){RootId="benchmark"})).Items[0];var collection=await catalog.CreateCollection("saved");await catalog.ChangeCollectionItems([collection.Id],[item],true);
        await catalog.RetireOperatedFile(item);
        Assert.False((await catalog.ReadCollectionFlags([item]))[0]);Assert.Single((await catalog.ReadFirstPage(new(){RootId="benchmark"})).Items);
    }
    [Fact]
    public async Task SlideshowRepeatsImagesAndStillSkipsVideosAndText()
    {
        string[] kinds=["video","image","text","image"];
        Task<bool> Image(int i,CancellationToken token)=>Task.FromResult(kinds[i]=="image");
        Assert.Null(await SlideshowSequence.Next(4,3,Image,CancellationToken.None));
        Assert.Equal(1,await SlideshowSequence.Next(4,3,Image,CancellationToken.None,repeat:true));
        Assert.Equal(3,await SlideshowSequence.Next(4,1,Image,CancellationToken.None,repeat:true));
        Assert.Null(await SlideshowSequence.Next(2,1,(i,t)=>Task.FromResult(false),CancellationToken.None,repeat:true));
    }
    [Fact]
    public async Task BasicImageProbeDoesNotLoadExifButExplicitDetailsStillWork()
    {
        string project=Project(),input=Path.Combine(project,"fixtures","public","sony-a7r4a-14bit.ARW");
        string exe=Path.Combine(project,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        await using var worker=new WorkerClient(exe,Temp());
        var basic=await worker.Request(input,"probe",new("fixture",1,1,1,1,1),new(ReadDetails:false),CancellationToken.None);
        Assert.True(basic.Message.Metadata!.Value.GetProperty("width").GetInt32()>0);
        Assert.False(basic.Message.Metadata.Value.TryGetProperty("details",out var details)&&details.ValueKind!=System.Text.Json.JsonValueKind.Null);
        var full=await worker.Request(input,"probe",new("fixture",1,1,1,1,1),new(ReadDetails:true),CancellationToken.None);
        Assert.Contains("SONY",full.Message.Metadata!.Value.GetProperty("details").GetProperty("cameraMake").GetString()!,StringComparison.OrdinalIgnoreCase);
    }
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-demand-tests",Guid.NewGuid().ToString("N"));
    private static string Project(){var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"FolderLens.slnx")))directory=directory.Parent;return directory!.FullName;}
}
