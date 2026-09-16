namespace FolderLens.Infrastructure;

/// <summary>One owned runtime workspace; only playlist mappings live outside it.</summary>
public sealed class BrowsingSessionStorage : IAsyncDisposable
{
    private readonly WorkerTemporaryFiles.Session owner;
    public string DirectoryPath=>owner.Path;
    public CatalogStore Catalog {get;}
    private BrowsingSessionStorage(WorkerTemporaryFiles.Session owner,string durableDirectory)
    {
        this.owner=owner;
        Catalog=new(Path.Combine(owner.Path,"catalog"),new SnapshotLimits(),Path.Combine(durableDirectory,"collections","playlists.sqlite"));
    }
    public static async Task<BrowsingSessionStorage> Open(string dataDirectory,CancellationToken cancellation=default)
    {
        string runtime=Path.Combine(Path.GetFullPath(dataDirectory),"runtime");
        var owner=await Task.Run(()=>
        {
            var recovered=WorkerTemporaryFiles.Recover(runtime);
            if(recovered.ActiveInstances>0||recovered.UnsafeEntries>0||recovered.RetainedBytes>0)
                throw new IOException("临时浏览数据仍被占用或无法安全清理，请关闭其他 FolderLens 窗口后重试。已有收藏保留。");
            // Preserve unknown legacy remnants, but permit a bounded recovery
            // workspace. Never accumulate arbitrary-sized copies on every retry.
            if(recovered.UnownedInstances>16||recovered.UnownedBytes>64L<<20)
                throw new IOException("临时数据中有较多无法确认归属的残留，请在数据目录中检查 runtime 文件夹。已有收藏保留。");
            return WorkerTemporaryFiles.Create(runtime,Guid.NewGuid().ToString("N"));
        },cancellation).ConfigureAwait(false);
        var session=new BrowsingSessionStorage(owner,dataDirectory);
        try
        {
            await session.Catalog.Initialize(cancellation).ConfigureAwait(false);
            await session.Catalog.Write(c=>{CompactBrowsingCatalog.Configure(c);return true;},cancellation).ConfigureAwait(false);
            await session.Catalog.ImportLegacyPlaylists(Path.Combine(dataDirectory,"catalog","catalog.sqlite"),cancellation).ConfigureAwait(false);
            return session;
        }
        catch{await session.DisposeAsync().ConfigureAwait(false);throw;}
    }
    public async ValueTask DisposeAsync()
    {
        await Catalog.DisposeAsync().ConfigureAwait(false);
        var cleanup=await Task.Run(()=>WorkerTemporaryFiles.ReleaseExited(owner)).ConfigureAwait(false);
        if(cleanup.UnsafeEntries>0||cleanup.RetainedBytes>0)throw new IOException("部分临时浏览数据未能清理，将在下次启动重试。");
    }
}
