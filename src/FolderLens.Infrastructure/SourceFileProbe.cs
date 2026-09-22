namespace FolderLens.Infrastructure;

/// <summary>Bounded, cancellable source attribute reads, including mapped drives, in the scan process.</summary>
public sealed class SourceFileProbe(string? executable=null,TimeSpan? timeout=null):IAsyncDisposable
{
    internal SourceFileProbe(ScanWorkerClient client):this(){worker=client;}
    private readonly SemaphoreSlim gate=new(1,1);
    private ScanWorkerClient? worker;
    private bool disposed;
    internal async Task<ScanDirectoryPacket> ReadPacket(string path,CancellationToken cancellation,bool allowCloud=false,SourceFileStamp? prepare=null,SourceParentAuthorization? parent=null)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            worker??=new(executable??ScanWorkerClient.FindExecutable()??throw new InvalidOperationException("缺少文件访问组件 FolderLens.Scan.Worker.exe。"),timeout);
            return await worker.Probe(path,cancellation,allowCloud,prepare,parent:parent).ConfigureAwait(false);
        }
        finally{gate.Release();}
    }
    public async Task<SourceFileStamp> Read(string path,CancellationToken cancellation,bool allowCloud=false)
    {
        var observation=await ReadObservation(path,cancellation,allowCloud).ConfigureAwait(false);
        return new(observation.Bytes,observation.Modified,FileObservationWriter.Signature(observation));
    }
    public async Task<ScanEntry> ReadObservation(string path,CancellationToken cancellation,bool allowCloud=false)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            worker??=new(executable??ScanWorkerClient.FindExecutable()??throw new InvalidOperationException("缺少文件访问组件 FolderLens.Scan.Worker.exe。"),timeout);
            var result=await worker.Probe(path,cancellation,allowCloud).ConfigureAwait(false);
            if(result.State=="present"&&result.FileStamp is {} stamp&&result.FileObservation is {} observation){stamp.Validate();return observation;}
            throw result.State switch
            {
                "missing"=>new FileNotFoundException("原文件已不存在。",path),
                "inaccessible"=>new UnauthorizedAccessException("无法访问原文件。"),
                "excluded"=>new IOException("原文件为在线占位文件或不支持的链接。"),
                _=>new IOException("原文件离线或不是普通文件。")
            };
        }
        finally{gate.Release();}
    }
    public async Task<string> ResolveImage(string root,string document,string relativeUrl,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            worker??=new(executable??ScanWorkerClient.FindExecutable()??throw new InvalidOperationException("缺少文件访问组件 FolderLens.Scan.Worker.exe。"),timeout);
            return await worker.ResolveImage(root,document,relativeUrl,cancellation).ConfigureAwait(false);
        }
        finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try{if(disposed)return;disposed=true;if(worker is not null)await worker.DisposeAsync().ConfigureAwait(false);}
        finally{gate.Release();}
    }
}
