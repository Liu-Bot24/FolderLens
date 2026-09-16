using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScanWorkerCursorTests
{
    [Fact]
    public async Task InterleavedDirectoriesKeepTheirPositionsInOneWorker()
    {
        string source=Fixture();
        foreach(string name in new[]{"A","B"})
        {
            Directory.CreateDirectory(Path.Combine(source,name));
            for(int i=0;i<300;i++)File.WriteAllText(Path.Combine(source,name,$"{name}-{i:D3}.jpg"),"image");
        }
        await using var worker=new ScanWorkerClient(Worker());
        await using var first=worker.ReadCursor(Path.Combine(source,"A"),false,CancellationToken.None).GetAsyncEnumerator();
        await using var second=worker.ReadCursor(Path.Combine(source,"B"),false,CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());int? process=worker.ProcessId;Assert.NotNull(process);
        Assert.True(await second.MoveNextAsync());Assert.Equal(process,worker.ProcessId);
        var names=new[]{new HashSet<string>(),new HashSet<string>()};var active=new[]{true,true};var cursors=new[]{first,second};
        while(active.Any(value=>value))
            for(int i=0;i<2;i++)
            {
                if(!active[i])continue;
                active[i]=await cursors[i].MoveNextAsync();if(!active[i])continue;
                foreach(var entry in cursors[i].Current.Entries)
                {Assert.StartsWith(i==0?"A-":"B-",entry.Name);Assert.True(names[i].Add(entry.Name));}
                Assert.Equal(process,worker.ProcessId);
            }
        Assert.All(names,items=>Assert.Equal(300,items.Count));
    }

    [Fact]
    public async Task CancellationRetiresWorkerAndAllPausedCursors()
    {
        string source=Fixture();File.WriteAllText(Path.Combine(source,"photo.jpg"),"image");
        using var stop=new CancellationTokenSource();await using var worker=new ScanWorkerClient(Worker());
        await using var first=worker.ReadCursor(source,false,stop.Token).GetAsyncEnumerator();
        await using var second=worker.ReadCursor(source,false,stop.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());Assert.True(await second.MoveNextAsync());Assert.NotNull(worker.ProcessId);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first.MoveNextAsync().AsTask());
        Assert.Null(worker.ProcessId);
    }

    [Fact]
    public async Task CursorAdmissionIsBounded()
    {
        string source=Fixture();await using var worker=new ScanWorkerClient(Worker());
        var cursors=new List<IAsyncEnumerator<ScanDirectoryPacket>>();
        try
        {
            for(int i=0;i<ScanWorkerProtocol.MaximumCursors;i++)
            {var cursor=worker.ReadCursor(source,false,CancellationToken.None).GetAsyncEnumerator();cursors.Add(cursor);Assert.True(await cursor.MoveNextAsync());}
            await using var overflow=worker.ReadCursor(source,false,CancellationToken.None).GetAsyncEnumerator();
            await Assert.ThrowsAsync<ScanWorkerUnavailableException>(()=>overflow.MoveNextAsync().AsTask());Assert.Null(worker.ProcessId);
        }
        finally{foreach(var cursor in cursors)await cursor.DisposeAsync();}
    }

    private static string Fixture(){string path=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static string Worker()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
        return Path.Combine(root!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
    }
}
