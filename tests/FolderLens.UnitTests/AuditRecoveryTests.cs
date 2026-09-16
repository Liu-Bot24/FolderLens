using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class AuditRecoveryTests
{
    private static string Temp(){string p=Path.Combine(Path.GetTempPath(),"FolderLens-audit-recovery",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
    [Fact]
    public async Task UnownedSmallWorkspaceDoesNotPermanentlyBlockStartupOrGetDeleted()
    {
        string data=Temp(),orphan=Path.Combine(data,"runtime",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(orphan);
        string evidence=Path.Combine(orphan,"unknown.txt");File.WriteAllText(evidence,"retain");
        await using var session=await BrowsingSessionStorage.Open(data);
        Assert.True(File.Exists(evidence));Assert.NotEqual(orphan,session.DirectoryPath);
    }
    [Fact]
    public async Task FreshInterruptedThumbnailWriteIsRecoveredAndDiskStatisticsAreHonest()
    {
        string owned=Path.Combine(Temp(),"cache");
        await using(var first=new ThumbnailCache(owned,new(){MinimumFreeBytes=0})){await first.Initialize();}
        string temporary=Path.Combine(owned,"."+new string('A',64)+"."+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllBytes(temporary,new byte[2<<20]);
        string unknown=Path.Combine(owned,"keep.txt");File.WriteAllText(unknown,"keep");
        await using var cache=new ThumbnailCache(owned,new(){MinimumFreeBytes=0});await cache.Initialize();
        Assert.False(File.Exists(temporary));Assert.True(File.Exists(unknown));
        Assert.Equal(Directory.EnumerateFiles(owned).Sum(p=>new FileInfo(p).Length),(await cache.GetStatistics()).TotalDiskBytes);
    }
    [Fact]
    public async Task PlaylistPathTimeoutDoesNotHideHealthyItemsOrRemoveMappings()
    {
        string data=Temp(),source=Path.Combine(data,"source");Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source,"bad.jpg"),"bad");File.WriteAllText(Path.Combine(source,"good.jpg"),"good");string id;
        await using(var first=await BrowsingSessionStorage.Open(data))
        {
            long epoch=await first.Catalog.OpenRoot("root",source);await new DirectoryIndexer(first.Catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            id=(await first.Catalog.CreateCollection("test")).Id;await first.Catalog.ChangeCollectionItems([id],(await first.Catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items,true);
        }
        await using var second=await BrowsingSessionStorage.Open(data);
        second.Catalog.PlaylistProbeOverride=(path,ct)=>path.EndsWith("bad.jpg")?Task.FromException<ScanDirectoryPacket>(new TimeoutException("path timeout")):Task.FromResult(ScanPathProbe.Read(path));
        await second.Catalog.RefreshPlaylist(id);
        Assert.Equal("good.jpg",Assert.Single((await second.Catalog.ReadFirstPage(new(){RootId="collection:"+id,CollectionId=id,Kinds=[]})).Items).RelativePath);
        Assert.Equal(2,Assert.Single(await second.Catalog.ReadCollections()).Count);
    }
    private sealed class InlineProgress(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    [Fact]
    public async Task ReconcilePublishesFiniteBatchWhileDirectoryKeepsChanging()
    {
        string data=Temp(),source=Path.Combine(data,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"a.jpg"),"a");
        await using var catalog=new CatalogStore(Path.Combine(data,"db"));await catalog.Initialize();long epoch=await catalog.OpenRoot("r",source);
        var scanner=new DirectoryIndexer(catalog);await scanner.Scan("r",source,epoch,true,[],null,CancellationToken.None);
        var dirty=new ScanDirtyDirectories(catalog);await dirty.Mark("r",epoch,[new("","test")]);
        int completions=0;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var progress=new InlineProgress(p=>{if(p.State=="ready"){Interlocked.Increment(ref completions);dirty.Mark("r",epoch,[new("","continued")]).GetAwaiter().GetResult();}});
        var result=await scanner.ReconcileDirty("r",source,epoch,true,[],progress,deadline.Token);
        Assert.False(deadline.IsCancellationRequested);Assert.Equal(1,completions);Assert.Equal("partial",result.State);Assert.Single(await dirty.Read("r",epoch));
        Assert.Single((await catalog.ReadFirstPage(new(){RootId="r",Kinds=[]})).Items);
    }
    [Fact]
    public async Task AttributeOnlyChangeNotifiesWithoutContentOrTimestampChange()
    {
        string directory=Temp(),file=Path.Combine(directory,"sample.jpg");File.WriteAllText(file,"sample");var modified=File.GetLastWriteTimeUtc(file);
        int notifications=0;using var monitor=new RootChangeMonitor(directory,()=>Interlocked.Increment(ref notifications));
        await Task.Delay(1800);Interlocked.Exchange(ref notifications,0);File.SetAttributes(file,FileAttributes.Hidden);
        await Task.Delay(1800);Assert.True(Volatile.Read(ref notifications)>0);Assert.Equal(modified,File.GetLastWriteTimeUtc(file));
    }
}
