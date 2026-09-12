using System.Runtime.CompilerServices;
using System.Text;

namespace FolderLens.Infrastructure;

public sealed record TextSearchQuery(string Literal,bool MatchCase=true,long StartOffset=0,bool Wrap=true,int MaxHits=10_000,int BatchSize=100,long QueryGeneration=0,long FileVersion=0);
public sealed record TextMatch(long ByteOffset,long ByteLength,string Preview);
public sealed record TextSearchBatch(IReadOnlyList<TextMatch> Matches,long QueryGeneration,long FileVersion,string VersionKey,long ScannedBytes,bool Wrapped,bool IsFinal,bool Complete,bool LimitReached);

public static class TextSearch
{
    /// <summary>Each MoveNext does bounded source work on a background thread; batches contain at most 100 matches.
    /// UI must compare QueryGeneration/FileVersion before applying a batch. Line/column lookup uses TextLineIndex.</summary>
    public static async IAsyncEnumerable<TextSearchBatch> SearchAsync(string path,TextSearchQuery query,string? encoding=null,[EnumeratorCancellation]CancellationToken cancellation=default)
    {
        using var reader=await Task.Run(()=>new BoundedTextReader(path,encoding,cancellation),cancellation).ConfigureAwait(false);
        using var iterator=Scan(reader,query,cancellation).GetEnumerator();
        while(await Task.Run(iterator.MoveNext,cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();yield return iterator.Current;
        }
    }
    internal static IEnumerable<TextSearchBatch> Scan(BoundedTextReader reader,TextSearchQuery query,CancellationToken cancellation)
    {
        if(string.IsNullOrEmpty(query.Literal)||query.Literal.Length>4096)throw new ArgumentException("搜索词长度需为 1–4096 字符。");
        // A literal cannot begin/end inside a Unicode scalar.
        try{new UTF8Encoding(false,true).GetByteCount(query.Literal);}catch(EncoderFallbackException ex){throw new ArgumentException("搜索词包含不完整的 Unicode 字符。",ex);}
        if(query.MaxHits is <1 or >10_000 || query.BatchSize is <1 or >100 || query.StartOffset<0)throw new ArgumentOutOfRangeException(nameof(query));
        cancellation.ThrowIfCancellationRequested();reader.CheckVersion();
        long start=Math.Max(reader.BomLength,Math.Min(query.StartOffset,reader.Length)),scanned=0;int hits=0;bool wrapped=false;
        var ranges=new List<(long Start,long End)>{(start,reader.Length)};
        if(query.Wrap && start>reader.BomLength)ranges.Add((reader.BomLength,start));
        var batch=new List<TextMatch>(query.BatchSize);
        TextSearchBatch Result(bool final=false,bool complete=false,bool limited=false)=>new(batch.ToArray(),query.QueryGeneration,query.FileVersion,reader.VersionKey,scanned,wrapped,final,complete,limited);
        for(int pass=0;pass<ranges.Count;pass++)
        {
            wrapped=pass!=0;var range=ranges[pass];if(range.Start==range.End)continue;
            long position=range.Start,previousEnd=-1,carryStart=0;string carry="";int[] carryMap=[];bool first=true;
            long stop=range.End+Math.Min(reader.Length-range.End,query.Literal.Length*4L);
            while(position<stop)
            {
                cancellation.ThrowIfCancellationRequested();
                var page=first?reader.ReadWindow(position,64*1024,cancellation):reader.ReadAtBoundary(position,64*1024,cancellation);first=false;
                scanned=checked(scanned+page.Next-page.Start);
                string combined=carry+page.Text;
                long ByteAt(int index)=>index<carry.Length?checked(carryStart+carryMap[index]):page.ByteOffsetAt(index-carry.Length);
                int index=0;
                while((index=combined.IndexOf(query.Literal,index,query.MatchCase?StringComparison.Ordinal:StringComparison.OrdinalIgnoreCase))>=0)
                {
                    cancellation.ThrowIfCancellationRequested();long offset=ByteAt(index),end=ByteAt(index+query.Literal.Length);
                    if(offset>=range.Start && offset<range.End && end>previousEnd)
                    {
                        int preview=Math.Min(120,combined.Length-index);
                        if(preview>0 && index+preview<combined.Length && char.IsHighSurrogate(combined[index+preview-1]))preview--;
                        batch.Add(new(offset,end-offset,combined.Substring(index,preview)));hits++;
                        if(hits==query.MaxHits){reader.CheckVersion();yield return Result(final:true,limited:true);yield break;}
                        if(batch.Count==query.BatchSize){reader.CheckVersion();yield return Result();batch.Clear();}
                    }
                    index++;
                }
                previousEnd=page.Next;
                int cut=combined.Length-Math.Min(query.Literal.Length-1,combined.Length);
                if(cut>0 && cut<combined.Length && char.IsLowSurrogate(combined[cut]) && char.IsHighSurrogate(combined[cut-1]))cut--;
                long nextCarryStart=ByteAt(cut);int[] nextMap=new int[combined.Length-cut+1];
                for(int n=0;n<nextMap.Length;n++)nextMap[n]=checked((int)(ByteAt(cut+n)-nextCarryStart));
                carry=combined[cut..];carryMap=nextMap;carryStart=nextCarryStart;
                if(page.Next<=position)throw new InvalidDataException("文本搜索未前进。");position=page.Next;
                reader.CheckVersion();yield return Result();batch.Clear();
            }
        }
        reader.CheckVersion();cancellation.ThrowIfCancellationRequested();yield return Result(final:true,complete:true);
    }
}
