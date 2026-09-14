using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScannerRegressionTests
{
    [Theory]
    [InlineData("rename",false)]
    [InlineData("rename",true)]
    [InlineData("move",false)]
    [InlineData("move",true)]
    [InlineData("missing",false)]
    [InlineData("missing",true)]
    public async Task ConfirmedRenameRejectsOldSelectionFromOverlappingRoot(string change,bool bulk)
    {
        string source=Fixture(),child=Path.Combine(source,"child");Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child,"before.txt"),"shared file");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();
        var scanner=new DirectoryIndexer(catalog,Worker());
        long parentEpoch=await catalog.OpenRoot("parent",source),childEpoch=await catalog.OpenRoot("child",child);
        await scanner.Scan("parent",source,parentEpoch,true,[],null,CancellationToken.None);
        await scanner.Scan("child",child,childEpoch,true,[],null,CancellationToken.None);
        var parent=(await catalog.ReadFirstPage(new(){RootId="parent",Kinds=[]})).Items.Single();
        var stale=(await catalog.ReadFirstPage(new(){RootId="child",Kinds=[]})).Items.Single();
        var oldSnapshot=await catalog.CreateSnapshot(new(){RootId="child",Kinds=[]},childEpoch,1);await catalog.RetainSnapshot(oldSnapshot.Id);
        string tag=(await catalog.CreateCollection("overlap rename")).Id;
        await catalog.ChangeCollectionItems([tag],[parent],true);
        Assert.True((await catalog.ReadCollectionFlags([stale])).Single());
        string destination=change=="rename"?Path.Combine(child,"after.txt"):Path.Combine(change=="move"?source:Fixture(),"after.txt");
        File.Move(Path.Combine(child,"before.txt"),destination);
        await scanner.Scan("parent",source,parentEpoch,true,[],null,CancellationToken.None);
        Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
        try{await Assert.ThrowsAsync<IOException>(()=>bulk?catalog.ChangeCollectionSelection([tag],oldSnapshot.Id,[new(0,1)],true):catalog.ChangeCollectionItems([tag],[stale],true));}
        finally{await catalog.ReleaseSnapshot(oldSnapshot.Id);}
        if(change!="rename")File.WriteAllText(Path.Combine(child,"fresh.txt"),"newly observed file");
        await scanner.Scan("child",child,childEpoch,true,[],null,CancellationToken.None);
        var fresh=(await catalog.ReadFirstPage(new(){RootId="child",Kinds=[]})).Items.Single();
        Assert.Equal(1,await catalog.ChangeCollectionItems([tag],[fresh],true));
    }
    [Fact] public async Task OpeningRenamedRootDoesNotInheritOldCollections()
    {
        string parent=Fixture(),before=Path.Combine(parent,"before"),after=Path.Combine(parent,"after");
        Directory.CreateDirectory(before);File.WriteAllText(Path.Combine(before,"file.txt"),"root rename");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();var scanner=new DirectoryIndexer(catalog,Worker());
        long epoch=await catalog.OpenRoot("before",before);await scanner.Scan("before",before,epoch,true,[],null,CancellationToken.None);
        var item=(await catalog.ReadFirstPage(new(){RootId="before",Kinds=[]})).Items.Single();
        string tag=(await catalog.CreateCollection("root rename")).Id;await catalog.ChangeCollectionItems([tag],[item],true);
        Directory.Move(before,after);
        epoch=await catalog.OpenRoot("after",after);await scanner.Scan("after",after,epoch,true,[],null,CancellationToken.None);
        Assert.Equal(0,(await catalog.CreateSnapshot(new(){RootId="after",Kinds=[],IncludeCollections=[tag]},epoch,1)).Count);
        Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
    }
    [Theory]
    [InlineData("offline")]
    [InlineData("inaccessible")]
    public async Task UnknownOldRootKeepsItsTagsWithoutGrantingThemToNewRoot(string failure)
    {
        string parent=Fixture(),before=Path.Combine(parent,"before"),after=Path.Combine(parent,"after");
        Directory.CreateDirectory(before);File.WriteAllText(Path.Combine(before,"file.txt"),"unknown root location");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();var scanner=new DirectoryIndexer(catalog,Worker());
        long epoch=await catalog.OpenRoot("old",before);await scanner.Scan("old",before,epoch,true,[],null,CancellationToken.None);
        var old=(await catalog.ReadFirstPage(new(){RootId="old",Kinds=[]})).Items.Single();
        string tag=(await catalog.CreateCollection("independent choices")).Id;await catalog.ChangeCollectionItems([tag],[old],true);
        Directory.Move(before,after);
        // Fault only the old-path metadata observation, never the actual directory
        // enumeration or identity of the newly opened generated root.
        scanner.PathProbeOverride=(path,cancellation)=>path==before?Task.FromResult(new ScanDirectoryPacket(failure,[],"InjectedUnavailable")):Task.Run(()=>ScanPathProbe.Read(path),cancellation);
        epoch=await catalog.OpenRoot("new",after);await scanner.Scan("new",after,epoch,true,[],null,CancellationToken.None);
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        long inherited=(await catalog.CreateSnapshot(new(){RootId="new",Kinds=[],IncludeCollections=[tag]},epoch,1)).Count;
        var current=(await catalog.ReadFirstPage(new(){RootId="new",Kinds=[]})).Items.Single();
        int explicitlyAdded=await catalog.ChangeCollectionItems([tag],[current],true);
        Assert.Equal(0,inherited);Assert.Equal(1,explicitlyAdded);
        Assert.Equal(2,(await catalog.ReadCollections()).Single().Count);
    }
    [Fact] public async Task ConfirmedRemovedExcludedSubtreeClearsCollections()
    {
        string source=Fixture(),child=Path.Combine(source,"child");Directory.CreateDirectory(child);File.WriteAllText(Path.Combine(child,"file.txt"),"excluded subtree");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();var scanner=new DirectoryIndexer(catalog,Worker());
        long epoch=await catalog.OpenRoot("excluded",source);await scanner.Scan("excluded",source,epoch,true,[],null,CancellationToken.None);
        var item=(await catalog.ReadFirstPage(new(){RootId="excluded",Kinds=[]})).Items.Single();
        string tag=(await catalog.CreateCollection("excluded subtree")).Id;await catalog.ChangeCollectionItems([tag],[item],true);
        await scanner.Scan("excluded",source,epoch,false,[],null,CancellationToken.None);
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        // Move generated fixture outside the scanned root: a complete parent
        // enumeration now confirms that this child no longer exists there.
        Directory.Move(child,Path.Combine(Fixture(),"moved"));
        await scanner.Scan("excluded",source,epoch,true,[],null,CancellationToken.None);
        Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
    }
    [Theory]
    [InlineData("😀", "B")]
    [InlineData("中文😀", "新目录😀")]
    [InlineData("普通中文", "新目录")]
    public async Task NonrecursiveRenamePreservesUnicodeDescendantPaths(string oldName,string newName)
    {
        string source=Fixture();Directory.CreateDirectory(Path.Combine(source,oldName,"子😀目录"));
        File.WriteAllText(Path.Combine(source,oldName,"inside.txt"),"direct child");
        File.WriteAllText(Path.Combine(source,oldName,"子😀目录","nested.txt"),"nested child");
        File.WriteAllText(Path.Combine(source,"root.txt"),"unrelated root file");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();long epoch=await catalog.OpenRoot("unicode",source);
        var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("unicode",source,epoch,true,[],null,CancellationToken.None);
        Directory.Move(Path.Combine(source,oldName),Path.Combine(source,newName));
        await scanner.Scan("unicode",source,epoch,false,[],null,CancellationToken.None);
        var rows=await catalog.Read(c=>
        {
            using var command=c.CreateCommand();command.CommandText="SELECT name,relative_path,entry_state FROM Files WHERE root_id='unicode'";
            using var reader=command.ExecuteReader();var values=new Dictionary<string,(string Path,string State)>();
            while(reader.Read())values.Add(reader.GetString(0),(reader.GetString(1),reader.GetString(2)));return values;
        });
        Assert.Equal((newName+"\\inside.txt","excluded"),rows["inside.txt"]);
        Assert.Equal((newName+"\\子😀目录\\nested.txt","excluded"),rows["nested.txt"]);
        Assert.Equal(("root.txt","present"),rows["root.txt"]);
        Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="unicode",Kinds=[],Recursive=false},epoch,1)).Count);
    }
    [Fact] public async Task DirectoryRenameWorkDoesNotScaleWithUnrelatedFiles()
    {
        string source=Fixture();Directory.CreateDirectory(Path.Combine(source,"A","child"));File.WriteAllText(Path.Combine(source,"A","child","kept.txt"),"rename fixture");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();long epoch=await catalog.OpenRoot("rename-scope",source);
        var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("rename-scope",source,epoch,true,[],null,CancellationToken.None);
        async Task<long> MeasureRename(string from,string to)
        {
            long steps=0;
            await catalog.Write(c=>{SQLitePCL.raw.sqlite3_progress_handler(c.Handle,100,_=>{steps++;return 0;},null);return true;});
            try{Directory.Move(Path.Combine(source,from),Path.Combine(source,to));await scanner.Scan("rename-scope",source,epoch,true,[],null,CancellationToken.None);return steps;}
            finally{await catalog.Write(c=>{SQLitePCL.raw.sqlite3_progress_handler(c.Handle,0,null,null);return true;});}
        }
        long small=await MeasureRename("A","B");
        await catalog.SeedBenchmark(5000);
        var current=await catalog.CreateSnapshot(new(){RootId="rename-scope",Kinds=[]},epoch,1);var item=(await catalog.ReadPage(current.Id,0)).Single();string tag=(await catalog.CreateCollection("renamed")).Id;await catalog.ChangeCollectionMembers([tag],[item.EntryId],true);
        long large=await MeasureRename("B","C");
        Assert.True(large<small*2+10,$"SQLite progress callbacks (100 VM instructions each): baseline={small}, with 5000 unrelated files={large}");
        Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
        Assert.Equal(5000,(await catalog.CreateSnapshot(new(){RootId="benchmark"},1,2)).Count);
    }
    [Fact] public async Task UnchangedRescanDoesNotRewriteCollectionLocationKeys()
    {
        string source=Fixture();File.WriteAllText(Path.Combine(source,"stable.txt"),"scan fixture");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();long epoch=await catalog.OpenRoot("stable",source);
        var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("stable",source,epoch,true,[],null,CancellationToken.None);
        var snapshot=await catalog.CreateSnapshot(new(){RootId="stable",Kinds=[]},epoch,1);var item=(await catalog.ReadPage(snapshot.Id,0)).Single();
        string tag=(await catalog.CreateCollection("stable membership")).Id;await catalog.ChangeCollectionMembers([tag],[item.EntryId],true);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="CREATE TEMP TABLE LocationWrites(value INTEGER);CREATE TEMP TRIGGER ObserveLocationWrites AFTER UPDATE OF location_key ON Files BEGIN INSERT INTO LocationWrites VALUES(1);END;";return cmd.ExecuteNonQuery();});
        await scanner.Scan("stable",source,epoch,true,[],null,CancellationToken.None);
        long writes=await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM LocationWrites";return (long)cmd.ExecuteScalar()!;});
        Assert.Equal(0,writes);
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        File.Move(Path.Combine(source,"stable.txt"),Path.Combine(source,"renamed.txt"));
        await scanner.Scan("stable",source,epoch,true,[],null,CancellationToken.None);
        Assert.Equal(0,(await catalog.ReadCollections()).Single().Count);
    }
    [Fact] public async Task ReopeningKnownDirectoryDoesNotResetObservedCaseMode()
    {
        string source=Fixture();File.WriteAllText(Path.Combine(source,"image.jpg"),"scan fixture");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();long epoch=await catalog.OpenRoot("case-mode",source);
        var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("case-mode",source,epoch,true,[],null,CancellationToken.None);
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="CREATE TEMP TABLE CaseTransitions(mode TEXT);CREATE TEMP TRIGGER ObserveCase AFTER UPDATE OF case_mode ON Directories WHEN OLD.case_mode<>NEW.case_mode BEGIN INSERT INTO CaseTransitions VALUES(NEW.case_mode);END;";return command.ExecuteNonQuery();});
        await scanner.Scan("case-mode",source,epoch,true,[],null,CancellationToken.None);
        var changed=await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="SELECT count(*) FROM CaseTransitions WHERE mode='unknown'";return (long)command.ExecuteScalar()!;});
        Assert.Equal(0,changed);
    }
    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback,object? state)
        {Interlocked.Increment(ref Posts);ThreadPool.QueueUserWorkItem(_=>callback(state));}
    }
    [Fact] public void MediaProcessLifecycleDoesNotRunOnTheCallingUiContext()
    {
        var previous=SynchronizationContext.Current;var context=new RecordingContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            // With no scan request the worker exits with a protocol error; exercise
            // the process-exit/error path as well as its asynchronous stream reads.
            Assert.Throws<InvalidDataException>(()=>BoundedProcess.Run(Worker(),[],TimeSpan.FromSeconds(5),4096,CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        }
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
        Assert.Equal(0,context.Posts);
    }
    [Fact] public async Task ReconciliationUsesDirectoryIndexesAndPreservesUnrelatedFiles()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source"),other=Path.Combine(fixture,"other");
        Directory.CreateDirectory(Path.Combine(source,"removed","deep"));Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(source,"removed","deep","gone.mp3"),"scan fixture");
        File.WriteAllText(Path.Combine(source,"kept.mp4"),"scan fixture");File.WriteAllText(Path.Combine(other,"kept.mp3"),"scan fixture");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source),otherEpoch=await catalog.OpenRoot("other",other);
        var scanner=new DirectoryIndexer(catalog,Worker());
        await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        await scanner.Scan("other",other,otherEpoch,true,[],null,CancellationToken.None);
        var plan=await catalog.Read(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+DirectoryIndexer.ReconcileRemovedFilesSql;
            cmd.Parameters.AddWithValue("$root","root");cmd.Parameters.AddWithValue("$dir",DirectoryIndexer.StablePathId("root",""));cmd.Parameters.AddWithValue("$scan","next");
            using var rows=cmd.ExecuteReader();var steps=new List<string>();while(rows.Read())steps.Add(rows.GetString(3));return steps;
        });
        Assert.DoesNotContain(plan,step=>step.Contains("SCAN Files")||step.Contains("AUTOMATIC"));
        Assert.Contains(plan,step=>step.Contains("IX_Files_Directory")&&step.Contains("directory_id=?"));
        Assert.Contains(plan,step=>step.Contains("SEARCH d")&&step.Contains("root_id=? AND parent_id=?"));
        Directory.Move(Path.Combine(source,"removed"),Path.Combine(fixture,"outside-scan"));
        await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        var entries=await catalog.Read(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT name,entry_state,file_version FROM Files";using var rows=cmd.ExecuteReader();
            var values=new Dictionary<string,(string State,long Version)>();while(rows.Read())values.Add(rows.GetString(0),(rows.GetString(1),rows.GetInt64(2)));return values;
        });
        Assert.Equal(("missing",2L),entries["gone.mp3"]);
        Assert.Equal(("present",1L),entries["kept.mp4"]);Assert.Equal(("present",1L),entries["kept.mp3"]);
    }
    [Fact] public async Task ScanProgressDoesNotWaitBehindSnapshotReader()
    {
        string source=Fixture();for(int i=0;i<12;i++)File.WriteAllText(Path.Combine(source,$"image{i}.jpg"),"scan metadata only");
        await using var catalog=new CatalogStore(Fixture());await catalog.Initialize();long epoch=await catalog.OpenRoot("reader-contention",source);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var stop=new CancellationTokenSource();
        var snapshot=catalog.Read(_=>{entered.Set();release.Wait(TimeSpan.FromSeconds(10));return true;});
        Assert.True(await Task.Run(()=>entered.Wait(TimeSpan.FromSeconds(5))));
        var scan=new DirectoryIndexer(catalog,Worker()).Scan("reader-contention",source,epoch,true,[],null,stop.Token);
        try
        {
            Assert.Same(scan,await Task.WhenAny(scan,Task.Delay(2000)));
            Assert.Equal("ready",(await scan).State);Assert.Equal(12,(await scan).Files);
        }
        finally{stop.Cancel();release.Set();await snapshot;try{await scan;}catch(OperationCanceledException){}}
    }
    private static string Fixture()
    {string path=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string Worker()
    {
        if(Environment.GetEnvironmentVariable("FOLDERLENS_SCAN_WORKER") is {} value)return value;
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
        return Path.Combine(root!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
    }
    [Fact] public async Task WorkerEnumeratesRealFilesAndCancellationReclaimsActualProcess()
    {
        string source=Fixture();for(int i=0;i<257;i++)File.WriteAllText(Path.Combine(source,$"中文😀{i}.txt"),"readonly source");
        await using var worker=new ScanWorkerClient(Worker());var packets=new List<ScanDirectoryPacket>();
        await foreach(var packet in worker.Read(source,false,CancellationToken.None))packets.Add(packet);
        Assert.Equal("started",packets[0].State);Assert.Equal("completed",packets[^1].State);Assert.Equal(257,packets.Sum(p=>p.Entries.Length));Assert.All(packets,p=>Assert.InRange(p.Entries.Length,0,128));
        using var cancellation=new CancellationTokenSource();int process=worker.ProcessId!.Value;
        await using var iterator=worker.Read(source,false,cancellation.Token).GetAsyncEnumerator();Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();var clock=Stopwatch.StartNew();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>{while(await iterator.MoveNextAsync()){};});
        Assert.Null(worker.ProcessId);Assert.True(clock.Elapsed<TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentException>(()=>Process.GetProcessById(process));
    }
    [Fact] public async Task IsolatedSourceStampDetectsChangesRejectsMissingAndRecoversAfterCancellation()
    {
        string directory=Fixture(),path=Path.Combine(directory,"source.txt");File.WriteAllText(path,"original");
        await using var probe=new SourceFileProbe(Worker());
        var first=await probe.Read(path,CancellationToken.None);
        Assert.Equal(new FileInfo(path).Length,first.Length);Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks,first.ModifiedUtcTicks);
        File.AppendAllText(path,"changed");var second=await probe.Read(path,CancellationToken.None);Assert.NotEqual(first,second);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>probe.Read(Path.Combine(directory,"absent.txt"),CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(()=>probe.Read(directory,CancellationToken.None));
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>probe.Read(path,stop.Token));
        Assert.Equal(second,await probe.Read(path,CancellationToken.None));
        Assert.Equal("originalchanged",File.ReadAllText(path));
    }
    [Fact] public async Task MarkdownResourceResolutionUsesIsolatedWorkerAndPreservesPathRules()
    {
        string root=Fixture(),document=Path.Combine(root,"note.md"),image=Path.Combine(root,"图片.png");
        File.WriteAllText(document,"![图片](图片.png)");File.WriteAllBytes(image,[1,2,3]);
        await using var probe=new SourceFileProbe(Worker());
        string resolved=await probe.ResolveImage(root,document,Uri.EscapeDataString("图片.png"),CancellationToken.None);
        Assert.EndsWith("图片.png",resolved);Assert.Equal(new byte[]{1,2,3},await File.ReadAllBytesAsync(resolved));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>probe.ResolveImage(root,document,"../outside.png",CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>probe.ResolveImage(root,document,"https://example.com/image.png",CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(()=>probe.ResolveImage(root,document,"missing.png",CancellationToken.None));
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>probe.ResolveImage(root,document,"图片.png",stop.Token));
        Assert.Equal(resolved,await probe.ResolveImage(root,document,"图片.png",CancellationToken.None));
        Assert.Equal(3,(await probe.Read(image,CancellationToken.None)).Length);
    }
    [Fact] public async Task ReopeningOngoingScanPreservesEpochButStillChecksPhysicalIdentity()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");Directory.CreateDirectory(source);
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();
        var resolver=new RootIdentityResolver(catalog,Worker());var first=await resolver.Open(source);
        var same=await resolver.Open(source,ongoingScan:(first.RootId,first.Epoch));Assert.Equal(first.RootId,same.RootId);Assert.Equal(first.Epoch,same.Epoch);
        Directory.Move(source,source+"-old");Directory.CreateDirectory(source);
        var replaced=await resolver.Open(source,ongoingScan:(first.RootId,first.Epoch));Assert.NotEqual(first.RootId,replaced.RootId);Assert.True(replaced.IdentityChanged);
        var reopened=await resolver.Open(source);Assert.Equal(replaced.Epoch+1,reopened.Epoch);
    }
    [Fact] public async Task RootReplacementRetainsOldIndexAndRejectsNewVolumeContext()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"old.jpg"),"old");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        var indexer=new DirectoryIndexer(catalog,Worker());await indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        Directory.Move(source,source+"-old");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"new.jpg"),"new");
        await Assert.ThrowsAsync<RootIdentityChangedException>(()=>indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None));
        var state=await catalog.Read(c=>{using var command=c.CreateCommand();command.CommandText="SELECT group_concat(relative_path) FROM Files WHERE entry_state='present'";return (string)command.ExecuteScalar()!;});
        Assert.Equal("old.jpg",state);
        var resolver=new RootIdentityResolver(catalog,Worker());var replacement=await resolver.Open(source);
        Assert.NotEqual("root",replacement.RootId);Assert.True(replacement.IdentityChanged);
        await indexer.Scan(replacement.RootId,source,replacement.Epoch,true,[],null,CancellationToken.None);
        Directory.Move(source,source+"-new");var offline=await resolver.Open(source);
        Assert.Equal(replacement.RootId,offline.RootId);Assert.Equal("offline",offline.Availability);
        Directory.Move(source+"-old",source);var restored=await resolver.Open(source);
        Assert.Equal("root",restored.RootId);Assert.True(restored.IdentityChanged);
    }
    [Fact] public async Task ForcedRefreshInvalidatesMetadataAndStableScanDoesNotModifySources()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");Directory.CreateDirectory(source);string path=Path.Combine(source,"a.jpg");File.WriteAllText(path,"fixture only");
        byte[] hash=SHA256.HashData(File.ReadAllBytes(path));DateTime write=File.GetLastWriteTimeUtc(path);
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);var indexer=new DirectoryIndexer(catalog,Worker());
        await indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        string id=DirectoryIndexer.StablePathId("root","a.jpg");Assert.True(await catalog.ApplyImageMetadata(id,1,"root",epoch,10,20,"JPEG",false,false,"test"));
        await indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        Assert.Equal(1,await Version(catalog));await indexer.Scan("root",source,epoch,true,[],null,CancellationToken.None,forceRefresh:true);Assert.Equal(2,await Version(catalog));
        Assert.Equal(0,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files WHERE display_width IS NOT NULL";return (long)cmd.ExecuteScalar()!;}));
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(path)));Assert.Equal(write,File.GetLastWriteTimeUtc(path));
    }
    [Fact] public void AllocationIdentitySharesHardLinksButRetainsCreationIdentity()
    {
        string source=Fixture(),a=Path.Combine(source,"a.txt"),b=Path.Combine(source,"b.txt");File.WriteAllText(a,"hardlink data");
        Assert.True(CreateHardLinkW(b,a,IntPtr.Zero));var original=FileAllocation.InspectMetadata(a);var linked=FileAllocation.InspectMetadata(b);
        Assert.NotNull(original.PhysicalIdentity);Assert.Equal(original.PhysicalIdentity,linked.PhysicalIdentity);Assert.NotNull(original.ChangeTime);Assert.True(original.Allocated>0);
        var records=ScanDirectoryReader.Read(source).SelectMany(p=>p.Entries).ToArray();Assert.Equal(2,records.Length);Assert.All(records,r=>Assert.Null(r.SkipReason));
    }
    [Fact] public void CloudClassificationDoesNotConfuseCloudTagWithLink()
    {
        Assert.True(FileAllocation.IsCloudTag(0x9000001A));Assert.True(FileAllocation.IsCloudTag(0x9000F01A));Assert.False(FileAllocation.IsCloudTag(0xA0000003));
        Assert.True(FileAllocation.IsDeferred(0x40000));Assert.True(FileAllocation.IsDeferred(0x400000));Assert.False(FileAllocation.IsDeferred((long)FileAttributes.Normal));
    }
    [Fact] public async Task AbandonedScanOwnerIsTerminatedWithoutRemovingItsIndexedFiles()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"kept.jpg"),"fixture");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
        var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        using var exited=Process.Start(new ProcessStartInfo(Worker()){UseShellExecute=false,CreateNoWindow=true})!;
        int pid=exited.Id;long started=exited.StartTime.ToUniversalTime().Ticks;await exited.WaitForExitAsync();
        await catalog.Write(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="""
            INSERT INTO ScanRuns(scan_id,root_id,root_epoch,state,started_utc_ticks) VALUES('interrupted','root',$epoch,'running',1);
            INSERT INTO ScanOwners VALUES('interrupted',$pid,$start);
            INSERT INTO DirectoryScans(scan_id,directory_id,state) SELECT 'interrupted',directory_id,'enumerating' FROM Directories WHERE root_id='root';
            UPDATE Roots SET scan_state='scanning' WHERE root_id='root';
            """;cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$pid",pid);cmd.Parameters.AddWithValue("$start",started);return cmd.ExecuteNonQuery();
        });
        await new RootIdentityResolver(catalog,Worker()).Open(source);
        Assert.Equal("failed",await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT state FROM ScanRuns WHERE scan_id='interrupted'";return (string)cmd.ExecuteScalar()!;}));
        Assert.Equal("PreviousProcessExited",await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT error_code FROM DirectoryScans WHERE scan_id='interrupted' LIMIT 1";return (string)cmd.ExecuteScalar()!;}));
        Assert.Equal(1,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files WHERE entry_state='present'";return (long)cmd.ExecuteScalar()!;}));
    }
    private static Task<long> Version(CatalogStore catalog)=>catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT file_version FROM Files LIMIT 1";return (long)cmd.ExecuteScalar()!;});
    [Fact] public async Task RenamePreservesIdentityAcrossSubtreesAndDoesNotMergeHardLinks()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");Directory.CreateDirectory(Path.Combine(source,"old","sub"));
        File.WriteAllText(Path.Combine(source,"old","sub","photo.jpg"),"photo");File.WriteAllText(Path.Combine(source,"first.jpg"),"linked");Assert.True(CreateHardLinkW(Path.Combine(source,"alias.jpg"),Path.Combine(source,"first.jpg"),IntPtr.Zero));
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);var scanner=new DirectoryIndexer(catalog,Worker());
        await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);var before=await Entries();
        string collection=(await catalog.CreateCollection("rename semantics")).Id;
        await catalog.ChangeCollectionMembers([collection],before.Values.Select(value=>value.Id).ToArray(),true);
        Assert.Equal(3,(await catalog.ReadCollections()).Single().Count);
        Directory.Move(Path.Combine(source,"old"),Path.Combine(source,"renamed"));File.Move(Path.Combine(source,"first.jpg"),Path.Combine(source,"moved.jpg"));File.WriteAllText(Path.Combine(source,"first.jpg"),"new occupant");
        await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);var after=await Entries();
        Assert.Equal(4,after.Count);Assert.Equal(before[@"old\sub\photo.jpg"].Id,after[@"renamed\sub\photo.jpg"].Id);Assert.True(after[@"renamed\sub\photo.jpg"].PathRevision>1);
        Assert.Equal(before["first.jpg"].Id,after["moved.jpg"].Id);Assert.Equal(before["alias.jpg"].Id,after["alias.jpg"].Id);Assert.NotEqual(after["moved.jpg"].Id,after["alias.jpg"].Id);Assert.NotEqual(after["moved.jpg"].Id,after["first.jpg"].Id);
        Assert.Equal(after["alias.jpg"].Physical,after["moved.jpg"].Physical);
        var collected=await catalog.CreateSnapshot(new(){RootId="collection:"+collection,CollectionId=collection,Kinds=[]},epoch,1);
        Assert.Equal("alias.jpg",Assert.Single(await catalog.ReadPage(collected.Id,0)).RelativePath);
        // An unavailable root is not evidence that its files were deleted.
        Directory.Move(source,source+"-offline");
        await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        Assert.Equal(1,(await catalog.ReadCollections()).Single().Count);
        Task<Dictionary<string,(string Id,long PathRevision,string? Physical)>> Entries()=>catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT relative_path,entry_id,path_revision,physical_identity FROM Files WHERE root_id='root' AND entry_state='present'";using var rows=cmd.ExecuteReader();var result=new Dictionary<string,(string,long,string?)>();while(rows.Read())result.Add(rows.GetString(0),(rows.GetString(1),rows.GetInt64(2),rows.IsDBNull(3)?null:rows.GetString(3)));return result;});
    }
    [Fact] public async Task DirtyReconciliationIsScopedAndCannotAcknowledgeNewerEvents()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");Directory.CreateDirectory(Path.Combine(source,"left","deep"));Directory.CreateDirectory(Path.Combine(source,"right"));File.WriteAllText(Path.Combine(source,"left","a.jpg"),"old");File.WriteAllText(Path.Combine(source,"right","b.jpg"),"unchanged");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        var dirty=new ScanDirtyDirectories(catalog);await dirty.Mark("root",epoch,[new("left","First")]);var old=(await dirty.Read("root",epoch)).Single();await dirty.Mark("root",epoch,[new("left","Newer")]);await dirty.Acknowledge("root",epoch,old);Assert.Single(await dirty.Read("root",epoch));
        File.WriteAllText(Path.Combine(source,"left","a.jpg"),"changed");var result=await scanner.ReconcileDirty("root",source,epoch,true,[],null,CancellationToken.None);Assert.Equal("ready",result.State);Assert.Equal(1,result.Directories);Assert.Empty(await dirty.Read("root",epoch));
        Assert.Equal(1,await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT file_version FROM Files WHERE relative_path=$path";cmd.Parameters.AddWithValue("$path",@"right\b.jpg");return (long)cmd.ExecuteScalar()!;}));
        await dirty.Mark("root",epoch,[new("left","Offline")]);var retry=(await dirty.Read("root",epoch)).Single();await dirty.RetryLater("root",epoch,retry);Assert.Empty(await dirty.Read("root",epoch));Assert.Single(await dirty.Read("root",epoch,includeDeferred:true));
    }
    [Fact] public async Task WatcherRecoversFromInitiallyUnavailableDirectory()
    {
        string fixture=Fixture(),source=Path.Combine(fixture,"source");int notifications=0;
        using var monitor=new RootChangeMonitor(source,()=>Interlocked.Increment(ref notifications));
        for(int i=0;i<100&&monitor.LastError is null;i++)await Task.Delay(20);
        Assert.Equal("WatcherOffline",monitor.LastError);Directory.CreateDirectory(source);
        for(int i=0;i<400&&monitor.LastError is not null;i++)await Task.Delay(20);
        Assert.Null(monitor.LastError);File.WriteAllText(Path.Combine(source,"event.txt"),"change");
        int prior=Volatile.Read(ref notifications);for(int i=0;i<100&&Volatile.Read(ref notifications)==prior;i++)await Task.Delay(20);
        Assert.True(Volatile.Read(ref notifications)>prior);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLinkW(string newName,string existing,IntPtr security);
}
