using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyCollectionBatches(string source,Dictionary<string,object> report)
    {
        string directory=Path.Combine(source,"batch");Directory.CreateDirectory(directory);
        for(int i=0;i<130;i++)File.Copy(Path.Combine(source,"A","image-00.png"),Path.Combine(directory,$"{i:D3}.png"));
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var collection=await catalog!.CreateCollection("batch");
        await catalog.ChangeCollectionItems([collection.Id],await catalog.ReadPage(resultHandle!.Id,0,256),true);
        int files=0,parents=0;
        await using var worker=new ScanWorkerClient(ScanWorkerClient.FindExecutable(ScanWorkerDirectory)??throw new FileNotFoundException("Scan worker missing."));
        var tailEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe=typeof(CatalogStore).GetProperty("PlaylistProbeOverride",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)??throw new InvalidOperationException("Playlist verification seam missing.");
        probe.SetValue(catalog,(Func<string,CancellationToken,Task<ScanDirectoryPacket>>)(async(path,token)=>
        {
            if(path==directory)parents++;
            else if(++files>1){tailEntered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            return await worker.Probe(path,token);
        }));
        Task? opening=null;
        try
        {
            opening=OpenRoot("collection:"+collection.Id);
            await tailEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(opening.IsCompleted||activeCollectionId!=collection.Id||results is null||results.Count<64)throw new InvalidOperationException("收藏首批仍在等待全量验证。");
            report["publishedWhileTailBlocked"]=results.Count;report["parentProbes"]=parents;
            if(parents!=3)throw new InvalidOperationException("首批没有完成父目录前后复核。");
            await OpenRoot(Path.Combine(source,"B"));await opening.WaitAsync(TimeSpan.FromSeconds(5));
            if(activeCollectionId is not null||BrowsedDirectory!=Path.Combine(source,"B"))throw new InvalidOperationException("收藏后台完成覆盖了后续目录。");
            report["navigationCancelsTailWithoutStalePublication"]=true;report["status"]="PASS";
        }
        finally{probe.SetValue(catalog,null);scanStop.Cancel();if(opening is not null)try{await opening;}catch(OperationCanceledException){}}
    }
}
