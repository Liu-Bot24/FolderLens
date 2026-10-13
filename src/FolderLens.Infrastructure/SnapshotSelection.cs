using FolderLens.Core;
using System.Text.Json;

namespace FolderLens.Infrastructure;

public sealed record SnapshotSelection(IReadOnlyList<OrdinalRange> Ranges,long? PreviewOrdinal);

public sealed partial class CatalogStore
{
    public Task<SnapshotSelection> MapSnapshotSelection(ResultHandle previous,ResultHandle next,IReadOnlyList<OrdinalRange> ranges,long? previewOrdinal,CancellationToken cancellation=default)
        =>sessionComparisonReader.Execute(c=>
    {
        if(previous.RootId!=next.RootId||previous.Epoch!=next.Epoch)throw new ArgumentException("仅能映射同一目录的选择。");
        const int maximumRanges=262144;
        if(ranges.Count>maximumRanges)throw new IOException("选区过于分散，请减少选区后重新排序。");
        var normalized=OrdinalSelection.Normalize(ranges,previous.Count);
        if(normalized.Count==0&&previewOrdinal is null)return new SnapshotSelection([],null);
        var mapped=new List<OrdinalRange>();
        using var command=c.CreateCommand();
        command.CommandText="""
            SELECT n.ordinal FROM json_each($ranges) r
            CROSS JOIN ResultItems o CROSS JOIN ResultItems n
            WHERE o.session_id=$old AND o.ordinal>=json_extract(r.value,'$[0]')
              AND o.ordinal<json_extract(r.value,'$[0]')+json_extract(r.value,'$[1]')
              AND n.session_id=$next AND n.entry_id=o.entry_id
            ORDER BY n.ordinal
            """;
        command.Parameters.AddWithValue("$old",previous.Id);command.Parameters.AddWithValue("$next",next.Id);
        command.Parameters.AddWithValue("$ranges",JsonSerializer.Serialize(normalized.Select(range=>new[]{range.Start,range.Count})));
        using(var rows=command.ExecuteReader())
            while(rows.Read())
            {
                cancellation.ThrowIfCancellationRequested();long ordinal=rows.GetInt64(0);
                if(mapped.Count>0&&mapped[^1].End==ordinal){var prior=mapped[^1];mapped[^1]=prior with{Count=prior.Count+1};}
                else
                {
                    if(mapped.Count==maximumRanges)throw new IOException("排序后的选区过于分散，请减少选区后重试。");
                    mapped.Add(new(ordinal,1));
                }
            }
        long? preview=null;
        if(previewOrdinal is {} current)
        {
            command.CommandText="SELECT n.ordinal FROM ResultItems o JOIN ResultItems n ON n.session_id=$next AND n.entry_id=o.entry_id WHERE o.session_id=$old AND o.ordinal=$ordinal";
            command.Parameters.AddWithValue("$ordinal",current);preview=command.ExecuteScalar() is long found?found:null;
        }
        return new SnapshotSelection(mapped,preview);
    },cancellation);
}
