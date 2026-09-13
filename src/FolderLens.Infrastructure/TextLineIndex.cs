using System.Buffers.Binary;
using System.Text;

namespace FolderLens.Infrastructure;

/// <summary>All line numbers and UTF-16 columns are 1-based. Bytes and line numbers are 64-bit.</summary>
public sealed record TextPosition(long ByteOffset,long LineNumber,long Utf16Column);
public sealed record TextIndexProgress(long IndexedBytes,long Length,long IndexedThroughLine,bool Complete,long Checkpoints)
{
    public long? TotalLines=>Complete?IndexedThroughLine:null;
}

/// <summary>Version/encoding-bound sparse disk index. No full line-offset array is resident.
/// Each asynchronous step performs at most a 256 KiB window on a background thread, so jump requests can interleave with background indexing.</summary>
public sealed class TextLineIndex : IAsyncDisposable
{
    private const int HeaderSize=128,RecordSize=32,PageBytes=256*1024;
    private readonly BoundedTextReader reader;
    private readonly TextIndexDirectory owner;
    private readonly FileStream index;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private TextPosition cursor,lastWritten;
    private long records;
    private bool disposed;
    private volatile TextIndexProgress progress;
    public string VersionKey {get;}
    public string EncodingName=>reader.EncodingName;
    public long FileVersion {get;}
    public bool RebuiltCorruptCache {get;private set;}
    public TextIndexProgress Progress=>progress;
    private TextLineIndex(string path,string cacheDirectory,string? encoding,long fileVersion,CancellationToken cancellation)
    {
        reader=new(path,encoding,cancellation);FileVersion=fileVersion;VersionKey=reader.Snapshot.Key(path,reader.EncodingName,fileVersion);
        try{owner=new(cacheDirectory);try{index=owner.OpenIndex(VersionKey,cancellation);}catch{owner.Dispose();throw;}}catch{reader.Dispose();throw;}
        cursor=lastWritten=new(reader.BomLength,1,1);progress=new(cursor.ByteOffset,reader.Length,1,cursor.ByteOffset==reader.Length,0);
        try
        {
            if(index.Length>TextIndexDirectory.MaximumIndexBytes)throw new IOException("文本行索引超过 64 MiB 缓存上限；仍可按字节阅读和搜索。");
            try{if(index.Length==0)Reset();else Load(cancellation);}
            catch(Exception ex) when(ex is InvalidDataException or EndOfStreamException){RebuiltCorruptCache=true;Reset();}
            reader.CheckVersion();UpdateProgress();
        }
        catch{index.Dispose();owner.Dispose();reader.Dispose();throw;}
    }
    public static Task<TextLineIndex> OpenAsync(string path,string cacheDirectory,string? encoding=null,long fileVersion=0,CancellationToken cancellation=default)
        =>Task.Run(()=>new TextLineIndex(path,cacheDirectory,encoding,fileVersion,cancellation),cancellation);

    /// <summary>Advances a bounded number of 256 KiB pages for a supervised IPC request.</summary>
    public async Task<TextIndexProgress> IndexStepAsync(int pages=4,CancellationToken cancellation=default)
    {
        if(pages is <1 or >16)throw new ArgumentOutOfRangeException(nameof(pages));
        for(int n=0;n<pages && !Progress.Complete;n++)await Step(cancellation).ConfigureAwait(false);
        await Verify(cancellation).ConfigureAwait(false);return Progress;
    }

    public async Task BuildToEndAsync(IProgress<TextIndexProgress>? progress=null,CancellationToken cancellation=default)
    {
        while(!Progress.Complete){await Step(cancellation).ConfigureAwait(false);progress?.Report(Progress);}
        await Verify(cancellation).ConfigureAwait(false);
    }
    public async Task<TextPosition?> EnsureLineAsync(long lineNumber,IProgress<TextIndexProgress>? progress=null,CancellationToken cancellation=default)
    {
        if(lineNumber<1)throw new ArgumentOutOfRangeException(nameof(lineNumber));
        while(Progress.IndexedThroughLine<lineNumber && !Progress.Complete){await Step(cancellation).ConfigureAwait(false);progress?.Report(Progress);}
        return await Run(()=>FindLine(lineNumber,cancellation),cancellation).ConfigureAwait(false);
    }
    public async Task<TextPosition> GetPositionAsync(long byteOffset,IProgress<TextIndexProgress>? progress=null,CancellationToken cancellation=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);byteOffset=Math.Max(reader.BomLength,Math.Min(byteOffset,reader.Length));
        while(Progress.IndexedBytes<byteOffset && !Progress.Complete){await Step(cancellation).ConfigureAwait(false);progress?.Report(Progress);}
        return await Run(()=>FindPosition(byteOffset,cancellation),cancellation).ConfigureAwait(false);
    }
    public async Task<TextWindow> ReadWindowAsync(long byteOffset,int maxBytes=256*1024,CancellationToken cancellation=default)
    {
        var position=await GetPositionAsync(byteOffset,cancellation:cancellation).ConfigureAwait(false);
        return await Run(()=>reader.ReadAtBoundary(position.ByteOffset,maxBytes,cancellation),cancellation).ConfigureAwait(false);
    }
    public async Task<TextWindow?> ReadLineWindowAsync(long lineNumber,int maxBytes=256*1024,CancellationToken cancellation=default)
    {
        var position=await EnsureLineAsync(lineNumber,cancellation:cancellation).ConfigureAwait(false);
        return position is null?null:await Run(()=>reader.ReadAtBoundary(position.ByteOffset,maxBytes,cancellation),cancellation).ConfigureAwait(false);
    }
    private Task Verify(CancellationToken cancellation)=>Run(()=>true,cancellation);
    private Task Step(CancellationToken cancellation)=>Run(()=>
    {
        if(cursor.ByteOffset==reader.Length)return true;
        // A step may emit one record per 4096 newlines and one final record.
        // Reserve before mutating cursor/records so a quota failure cannot corrupt the index.
        if(index.Length>TextIndexDirectory.MaximumIndexBytes-(PageBytes/4096+1)*RecordSize)
            throw new IOException("文本行索引达到 64 MiB 缓存上限；仍可按字节阅读和搜索。");
        var page=reader.ReadAtBoundary(cursor.ByteOffset,PageBytes,cancellation);
        cursor=Advance(page,cursor,page.Text.Length,p=>
        {
            if(p.LineNumber-lastWritten.LineNumber>=4096 || p.ByteOffset-lastWritten.ByteOffset>=1024*1024)Append(p);
        });
        if(cursor.ByteOffset-lastWritten.ByteOffset>=1024*1024 || cursor.ByteOffset==reader.Length)Append(cursor);
        UpdateProgress();return true;
    },cancellation);
    private async Task<T> Run<T>(Func<T> action,CancellationToken cancellation)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try{return await Task.Run(()=>{ObjectDisposedException.ThrowIf(disposed,this);linked.Token.ThrowIfCancellationRequested();reader.CheckVersion();T result=action();reader.CheckVersion();linked.Token.ThrowIfCancellationRequested();return result;},linked.Token).ConfigureAwait(false);}
        finally{gate.Release();}
    }
    private TextPosition? FindLine(long requested,CancellationToken cancellation)
    {
        if(Progress.Complete && requested>cursor.LineNumber)return null;
        long lo=0,hi=records;
        while(lo<hi){long mid=lo+(hi-lo)/2;if(Read(mid).LineNumber<requested)lo=mid+1;else hi=mid;}
        TextPosition value=lo<records?Read(lo):Read(records-1);
        if(value.LineNumber==requested && value.Utf16Column==1)return value;
        value=Read(Math.Max(0,lo-1));
        while(value.ByteOffset<reader.Length)
        {
            cancellation.ThrowIfCancellationRequested();var page=reader.ReadAtBoundary(value.ByteOffset,PageBytes,cancellation);TextPosition? found=null;
            var next=Advance(page,value,page.Text.Length,p=>{if(found is null && p.LineNumber==requested)found=p;});
            if(found is not null)return found;value=next;
        }
        return value.LineNumber==requested && value.Utf16Column==1?value:null;
    }
    private TextPosition FindPosition(long requested,CancellationToken cancellation)
    {
        long lo=0,hi=records;
        while(lo<hi){long mid=lo+(hi-lo)/2;if(Read(mid).ByteOffset<=requested)lo=mid+1;else hi=mid;}
        var value=Read(Math.Max(0,lo-1));
        while(value.ByteOffset<requested)
        {
            cancellation.ThrowIfCancellationRequested();var page=reader.ReadAtBoundary(value.ByteOffset,PageBytes,cancellation);
            if(page.Next<=requested){value=Advance(page,value,page.Text.Length);continue;}
            int left=0,right=page.Text.Length;
            while(left<right){int mid=left+(right-left+1)/2;if(page.ByteOffsetAt(mid)<=requested)left=mid;else right=mid-1;}
            while(left>0 && page.ByteOffsetAt(left)==page.ByteOffsetAt(left-1))left--;
            return Advance(page,value,left);
        }
        return value;
    }
    private static TextPosition Advance(TextWindow page,TextPosition position,int count,Action<TextPosition>? newline=null)
    {
        int offset=0;long line=position.LineNumber,column=position.Utf16Column;
        while(offset<count)
        {
            int next=page.Text.AsSpan(offset,count-offset).IndexOfAny('\r','\n');
            if(next<0){column=checked(column+count-offset);break;}
            column=checked(column+next);offset+=next;
            int end=offset+(page.Text[offset]=='\r' && offset+1<page.Text.Length && page.Text[offset+1]=='\n'?2:1);
            if(end>count){column=checked(column+count-offset);break;}
            offset=end;line=checked(line+1);column=1;newline?.Invoke(new(page.ByteOffsetAt(offset),line,column));
        }
        return new(page.ByteOffsetAt(count),line,column);
    }
    private void UpdateProgress()=>progress=new(cursor.ByteOffset,reader.Length,cursor.LineNumber,cursor.ByteOffset==reader.Length,records);
    private void Reset()
    {
        index.SetLength(0);Span<byte> header=stackalloc byte[HeaderSize];header.Clear();"FLTEXT01"u8.CopyTo(header);Encoding.ASCII.GetBytes(VersionKey,header[8..72]);
        index.Write(header);records=0;cursor=lastWritten=new(reader.BomLength,1,1);Append(cursor,initial:true);index.Flush(true);
    }
    private void Load(CancellationToken cancellation)
    {
        if(index.Length<HeaderSize+RecordSize || (index.Length-HeaderSize)%RecordSize!=0)throw new InvalidDataException("文本行索引长度无效。");
        Span<byte> header=stackalloc byte[HeaderSize];index.Position=0;index.ReadExactly(header);
        if(!header[..8].SequenceEqual("FLTEXT01"u8)||Encoding.ASCII.GetString(header[8..72])!=VersionKey)throw new InvalidDataException("文本行索引版本无效。");
        records=(index.Length-HeaderSize)/RecordSize;TextPosition? previous=null;
        for(long n=0;n<records;n++)
        {
            cancellation.ThrowIfCancellationRequested();var item=Read(n);
            if(item.ByteOffset<reader.BomLength || item.ByteOffset>reader.Length || item.LineNumber<1 || item.Utf16Column<1 ||
                (previous is null && item!=new TextPosition(reader.BomLength,1,1)) ||
                (previous is not null && (item.ByteOffset<=previous.ByteOffset || item.LineNumber<previous.LineNumber || (item.LineNumber==previous.LineNumber&&item.Utf16Column<previous.Utf16Column))))throw new InvalidDataException("文本行索引记录无效。");
            previous=item;
        }
        cursor=lastWritten=previous!;
    }
    private TextPosition Read(long ordinal)
    {
        Span<byte> data=stackalloc byte[RecordSize];index.Position=checked(HeaderSize+ordinal*RecordSize);index.ReadExactly(data);
        if(BinaryPrimitives.ReadUInt64LittleEndian(data[24..])!=Checksum(data[..24]))throw new InvalidDataException("文本行索引校验失败。");
        return new(BinaryPrimitives.ReadInt64LittleEndian(data),BinaryPrimitives.ReadInt64LittleEndian(data[8..]),BinaryPrimitives.ReadInt64LittleEndian(data[16..]));
    }
    private void Append(TextPosition position,bool initial=false)
    {
        if(!initial && position.ByteOffset==lastWritten.ByteOffset)return;
        Span<byte> data=stackalloc byte[RecordSize];BinaryPrimitives.WriteInt64LittleEndian(data,position.ByteOffset);BinaryPrimitives.WriteInt64LittleEndian(data[8..],position.LineNumber);BinaryPrimitives.WriteInt64LittleEndian(data[16..],position.Utf16Column);BinaryPrimitives.WriteUInt64LittleEndian(data[24..],Checksum(data[..24]));
        index.Position=checked(HeaderSize+records*RecordSize);index.Write(data);index.Flush();records++;lastWritten=position;
    }
    private static ulong Checksum(ReadOnlySpan<byte> data){ulong hash=14695981039346656037;foreach(byte value in data)hash=unchecked((hash^value)*1099511628211);return hash;}
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;lifetime.Cancel();await gate.WaitAsync().ConfigureAwait(false);
        try{if(disposed)return;disposed=true;index.Dispose();reader.Dispose();owner.Dispose();}finally{gate.Release();}
    }
}
