using System.Text;

namespace FolderLens.Infrastructure;

public sealed record TextWindow(long Start,long Next,long Length,string Text,string Encoding,bool AtEnd)
{
    internal int[] OriginalByteOffsets {get;init;}=[];
    /// <summary>UTF-16 index to the exact original source byte. A surrogate pair maps to its scalar start.</summary>
    public long ByteOffsetAt(int utf16Index)
    {
        if(utf16Index<0 || utf16Index>Text.Length)throw new ArgumentOutOfRangeException(nameof(utf16Index));
        return checked(Start+OriginalByteOffsets[utf16Index]);
    }
}
public sealed record TextHit(long ByteOffset,string Preview);

/// <summary>Read-only bounded windows. Synchronous APIs belong on a background worker, never the UI thread.</summary>
public sealed class BoundedTextReader : IDisposable
{
    public const int MaxWindowBytes=1024*1024;
    private readonly FileStream stream;
    private readonly Encoding encoding;
    private readonly string path;
    private readonly SortedSet<long> checkpoints=[];
    public long Length=>Snapshot.Length;
    public string EncodingName=>encoding.WebName;
    public int BomLength {get;}
    public TextFileSnapshot Snapshot {get;}
    public string VersionKey=>Snapshot.Key(path,EncodingName);
    public BoundedTextReader(string path,string? selectedEncoding=null,CancellationToken cancellation=default,FolderLens.Contracts.ApprovedInput? approved=null,int detectionBytes=256*1024)
    {
        if(detectionBytes is <16 or >256*1024)throw new ArgumentOutOfRangeException(nameof(detectionBytes));
        this.path=Path.GetFullPath(path);
        stream=new FileStream(this.path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,detectionBytes<64*1024?1:64*1024,FileOptions.RandomAccess|FileOptions.Asynchronous);
        try
        {
            cancellation.ThrowIfCancellationRequested();Snapshot=TextFileSnapshot.Capture(stream);
            approved?.Observe(new FolderLens.Contracts.FileReadObservation(Snapshot.Length,DateTime.FromFileTimeUtc(Snapshot.LastWriteTicks).Ticks,Snapshot.SourceSignature!));
            byte[] sample=new byte[(int)Math.Min(detectionBytes,Length)];ReadExactly(sample,cancellation);
            var selection=TextEncodingPolicy.Detect(sample,Length==sample.Length,selectedEncoding);encoding=selection.Encoding;BomLength=selection.BomLength;
            checkpoints.Add(BomLength);CheckVersion();
        }
        catch{stream.Dispose();throw;}
    }
    public TextWindow ReadWindow(long requestedOffset,int maxBytes=256*1024,CancellationToken cancellation=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedOffset);
        CheckVersion();cancellation.ThrowIfCancellationRequested();
        return ReadAtBoundary(Align(Math.Min(requestedOffset,Length),cancellation),maxBytes,cancellation);
    }
    // Only internal index/search code may assert a previously validated character boundary.
    internal TextWindow ReadAtBoundary(long start,int maxBytes,CancellationToken cancellation)
    {
        if(maxBytes is <16 or >MaxWindowBytes)throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if(start<BomLength || start>Length)throw new ArgumentOutOfRangeException(nameof(start));
        CheckVersion();cancellation.ThrowIfCancellationRequested();stream.Position=start;
        byte[] bytes=new byte[(int)Math.Min(maxBytes,Length-start)];ReadExactly(bytes,cancellation);
        int usable=bytes.Length;string text;
        while(true)
        {
            try{text=encoding.GetString(bytes,0,usable);break;}
            catch(DecoderFallbackException) when(usable>0 && start+bytes.Length<Length && bytes.Length-usable<4){usable--;}
        }
        // Keeping CRLF together makes each sparse checkpoint an independently restartable line state.
        if(text.EndsWith('\r') && start+usable<Length && text.Length>1){usable-=encoding.GetByteCount("\r");text=text[..^1];}
        if(usable==0 && bytes.Length!=0)throw new InvalidDataException("无法找到有效编码边界。");
        long next=checked(start+usable);Remember(next);CheckVersion();cancellation.ThrowIfCancellationRequested();
        return new(start,next,Length,text,EncodingName,next==Length){OriginalByteOffsets=TextEncodingPolicy.MapCharacters(bytes.AsSpan(0,usable),text.Length,encoding)};
    }
    private void ReadExactly(Memory<byte> destination,CancellationToken cancellation)=>stream.ReadExactlyAsync(destination,cancellation).AsTask().GetAwaiter().GetResult();
    private void Remember(long boundary)
    {
        checkpoints.Add(boundary);
        if(checkpoints.Count>8192)checkpoints.Remove(checkpoints.Skip(1).First());
    }
    private long Align(long offset,CancellationToken cancellation)
    {
        offset=Math.Max(offset,BomLength);if(offset==Length)return offset;
        if(encoding.CodePage==65001)
        {
            for(int n=0;n<3 && offset>BomLength;n++){stream.Position=offset;if((stream.ReadByte()&0xC0)!=0x80)break;offset--;}
            return offset;
        }
        if(encoding.CodePage is 1200 or 1201)
        {
            offset-=(offset-BomLength)%2;stream.Position=offset;int first=stream.ReadByte(),second=stream.ReadByte();int unit=encoding.CodePage==1200?first|second<<8:first<<8|second;
            if(unit is >=0xDC00 and <=0xDFFF && offset>=BomLength+2)offset-=2;return offset;
        }
        if(encoding.IsSingleByte)return offset;
        // DBCS/GB18030 have ambiguous lead bytes: advance from a known boundary, never guess from a trailing byte.
        long current=checkpoints.GetViewBetween(BomLength,offset).Max;byte[] buffer=new byte[64*1024];
        while(current<offset)
        {
            cancellation.ThrowIfCancellationRequested();stream.Position=current;int wanted=(int)Math.Min(buffer.Length,offset-current);ReadExactly(buffer.AsMemory(0,wanted),cancellation);int valid=wanted;
            while(valid>0){try{encoding.GetCharCount(buffer,0,valid);break;}catch(DecoderFallbackException) when(wanted-valid<4){valid--;}}
            if(valid==0)break;current+=valid;Remember(current);
        }
        return current;
    }
    public IEnumerable<TextHit> Search(string literal,bool matchCase=true,long startOffset=0,int maxHits=10_000,CancellationToken cancellation=default)
    {
        foreach(var batch in TextSearch.Scan(this,new(literal,matchCase,startOffset,Wrap:false,MaxHits:maxHits),cancellation))
            foreach(var hit in batch.Matches)yield return new(hit.ByteOffset,hit.Preview);
    }
    public void CheckVersion()=>Snapshot.Validate(path,stream);
    public void Dispose()=>stream.Dispose();
}
