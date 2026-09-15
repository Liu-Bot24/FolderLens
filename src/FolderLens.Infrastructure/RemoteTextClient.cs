using System.Text.Json;
using System.Text;
using System.Runtime.CompilerServices;
using FolderLens.Contracts;

namespace FolderLens.Infrastructure;

/// <summary>UI-side text client. Performs no source FileInfo/Open/Read, including during first use or cancellation.
/// The supplied WorkerClient must target FolderLens.Content.Worker and a local application-owned task directory.</summary>
public sealed class RemoteTextClient
{
    private readonly bool allowCloud;
    private readonly WorkerClient worker;
    private readonly string path;
    private readonly RequestContext context;
    private readonly string? encoding;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly object searchGate=new();
    private CancellationTokenSource? activeSearch;
    private SourceFileStamp? stamp;
    private TextFileSnapshot? snapshot;
    public string? VersionKey {get;private set;}
    public string? EncodingName {get;private set;}
    public TextIndexProgress? IndexProgress {get;private set;}
    public SourceFileStamp? SourceStamp=>stamp;
    public const int MaxCopyCharacters=16*1024*1024/sizeof(char);
    public RemoteTextClient(WorkerClient worker,string path,RequestContext context,string? encoding=null,SourceFileStamp? sourceStamp=null,bool allowCloud=false)
    {
        this.allowCloud=allowCloud;this.worker=worker;this.path=Path.GetFullPath(path);this.context=context;this.encoding=encoding;stamp=sourceStamp;
    }
    public async Task<TextWindow> ReadWindow(long byteOffset,int maxBytes=64*1024,CancellationToken cancellation=default)
    {
        if(byteOffset<0 || maxBytes is <16 or >64*1024)throw new ArgumentOutOfRangeException(nameof(byteOffset));
        var reply=await Send("textWindow",new(encoding,ByteOffset:byteOffset,MaxBytes:maxBytes),cancellation).ConfigureAwait(false);
        var page=reply.Window??throw new InvalidDataException("文本工作进程未返回窗口。");
        if(page.Start<0 || page.Next<page.Start || page.Next>page.Length || page.Next-page.Start>maxBytes || page.OriginalByteOffsets.Length!=page.Text.Length+1 || page.OriginalByteOffsets[0]!=0 || page.OriginalByteOffsets[^1]!=page.Next-page.Start)
            throw new InvalidDataException("文本工作进程窗口边界无效。");
        int previous=0;foreach(int offset in page.OriginalByteOffsets){if(offset<previous || offset>page.Next-page.Start)throw new InvalidDataException("文本字节映射无效。");previous=offset;}
        return new(page.Start,page.Next,page.Length,page.Text,page.Encoding,page.AtEnd){OriginalByteOffsets=page.OriginalByteOffsets};
    }
    public async Task<string> ReadDocumentForCopy(int maxCharacters=MaxCopyCharacters,IProgress<long>? progress=null,CancellationToken cancellation=default)
    {
        if(maxCharacters is <1 or >MaxCopyCharacters)throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var text=new StringBuilder();long offset=0;
        while(true)
        {
            var page=await ReadWindow(offset,cancellation:cancellation).ConfigureAwait(false);
            if(page.Text.Length>maxCharacters-text.Length)throw new InvalidOperationException("全文超过复制上限（16 MiB 字符数据），请缩小选择范围。");
            text.Append(page.Text);progress?.Report(page.Next);cancellation.ThrowIfCancellationRequested();
            if(page.AtEnd)return text.ToString();
            if(page.Next<=offset)throw new InvalidDataException("文本读取未前进，已停止复制。");
            offset=page.Next;
        }
    }
    public async IAsyncEnumerable<TextSearchBatch> FindAll(string literal,bool matchCase=true,int maxHits=10_000,[EnumeratorCancellation]CancellationToken cancellation=default)
    {
        if(string.IsNullOrEmpty(literal)||literal.Length>4096||maxHits is <1 or >10_000)throw new ArgumentException("搜索条件无效。");
        using var own=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock(searchGate){activeSearch?.Cancel();activeSearch=own;}
        try
        {
            var options=new TextWorkerParameters(encoding,Literal:literal,MatchCase:matchCase,Wrap:false,SearchToken:Guid.NewGuid().ToString("N"),MaxHits:maxHits);
            while(true)
            {
                var reply=await Send("textFind",options,own.Token).ConfigureAwait(false);
                var batch=reply.Search??throw new InvalidDataException("文本工作进程未返回搜索状态。");
                if(batch.QueryGeneration!=context.QueryGeneration||batch.FileVersion!=context.FileVersion||batch.VersionKey!=reply.VersionKey||batch.Matches.Count>32)throw new InvalidDataException("文本搜索返回了过期或超限数据。");
                own.Token.ThrowIfCancellationRequested();yield return batch;
                if(batch.IsFinal)yield break;
            }
        }
        finally{lock(searchGate){if(ReferenceEquals(activeSearch,own))activeSearch=null;}}
    }
    public async Task<RemoteTextFindResult> FindNext(string literal,long startOffset=0,bool matchCase=true,bool wrap=true,IProgress<TextSearchBatch>? progress=null,CancellationToken cancellation=default)
    {
        if(string.IsNullOrEmpty(literal) || literal.Length>4096 || startOffset<0)throw new ArgumentException("搜索词或起始位置无效。");
        using var own=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock(searchGate){activeSearch?.Cancel();activeSearch=own;}
        try
        {
            string token=Guid.NewGuid().ToString("N");var options=new TextWorkerParameters(encoding,ByteOffset:startOffset,Literal:literal,MatchCase:matchCase,Wrap:wrap,SearchToken:token);
            while(true)
            {
                var reply=await Send("textFind",options,own.Token).ConfigureAwait(false);
                var result=reply.Search??throw new InvalidDataException("文本工作进程未返回搜索状态。");
                if(result.QueryGeneration!=context.QueryGeneration || result.FileVersion!=context.FileVersion || result.VersionKey!=reply.VersionKey || result.Matches.Count>1)throw new InvalidDataException("文本搜索返回了过期上下文。");
                progress?.Report(result);own.Token.ThrowIfCancellationRequested();
                if(result.Matches.Count>0)return new(result.Matches[0],result.Wrapped,false,result.ScannedBytes,reply.VersionKey);
                if(result.IsFinal)return new(null,result.Wrapped,result.Complete,result.ScannedBytes,reply.VersionKey);
            }
        }
        finally{lock(searchGate){if(ReferenceEquals(activeSearch,own))activeSearch=null;}}
    }
    public async Task<TextPosition?> FindLine(long lineNumber,IProgress<TextIndexProgress>? progress=null,CancellationToken cancellation=default)
    {
        if(lineNumber<1)throw new ArgumentOutOfRangeException(nameof(lineNumber));
        while(true)
        {
            var reply=await Send("textLinePosition",new(encoding,LineNumber:lineNumber),cancellation).ConfigureAwait(false);
            if(reply.Index is {} state)progress?.Report(state);
            if(reply.LineResolved)return reply.Position;
            cancellation.ThrowIfCancellationRequested();
        }
    }
    public async Task<TextIndexProgress> IndexStep(int pages=4,CancellationToken cancellation=default)
        =>(await Send("textIndexStep",new(encoding,StepPages:pages),cancellation).ConfigureAwait(false)).Index??throw new InvalidDataException("文本工作进程未返回索引状态。");
    private async Task<TextWorkerResponse> Send(string operation,TextWorkerParameters parameters,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ImageReply reply;
            try{reply=await worker.RequestData(path,operation,context,parameters with{ExpectedSnapshot=snapshot},cancellation,stamp,allowCloud).ConfigureAwait(false);}
            catch(InvalidDataException ex) when(ex.Message=="FileChanged"){throw new IOException("文本文件已变化，请重新加载。",ex);}
            if(reply.AssetPath is not null || reply.Message.AssetToken is not null)throw new InvalidDataException("文本数据不能作为图片缓存返回。");
            var result=reply.Message.Metadata?.Deserialize<TextWorkerResponse>(WorkerProtocol.Json)??throw new InvalidDataException("文本工作进程返回了无效数据。");
            if(!result.IsFinal || result.VersionKey!=result.Snapshot.Key(path,result.Encoding,context.FileVersion) || (snapshot is not null && result.Snapshot!=snapshot))throw new IOException("文本文件已变化，请重新加载。");
            snapshot=result.Snapshot;stamp=new(snapshot.Length,DateTime.FromFileTimeUtc(snapshot.LastWriteTicks).Ticks);VersionKey=result.VersionKey;EncodingName=result.Encoding;IndexProgress=result.Index;
            return result;
        }
        finally{gate.Release();}
    }
}
