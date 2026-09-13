using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record SnapshotSplice(int Prefix,int Removed,int Added)
{
    public IReadOnlyList<RangeEdit> Preserve(IEnumerable<(int Old,int New)> anchors)
    {
        var edits=new List<RangeEdit>();int old=Prefix,next=Prefix;
        foreach(var anchor in anchors.OrderBy(item=>item.Old))
        {
            if(anchor.Old<old||anchor.New<next||anchor.Old>=Prefix+Removed||anchor.New>=Prefix+Added)continue;
            if(anchor.Old>old||anchor.New>next)edits.Add(new(old,anchor.Old-old,anchor.New-next));
            old=anchor.Old+1;next=anchor.New+1;
        }
        if(old<Prefix+Removed||next<Prefix+Added)edits.Add(new(old,Prefix+Removed-old,Prefix+Added-next));
        return edits;
    }
    public int? MapOldIndex(int index)=>index<Prefix?index:index>=Prefix+Removed?index-Removed+Added:null;
    public int MapFinalToRemovalIndex(int index)=>index<Prefix?index:index>=Prefix+Added?index-Added:-1;
}
public sealed partial class CatalogStore
{
    // Refine insertion/deletion-only histories without materializing FileRows. The
    // unique session/entry index matches identities; SQL runs on the database lane.
    // A reordered match declines refinement, leaving visible-anchor handling intact.
    public Task<IReadOnlyDictionary<string,IReadOnlyList<RangeEdit>?>> RefineSnapshotChanges(ResultHandle previous,ResultHandle next,IReadOnlyList<SnapshotGroup> previousGroups,IReadOnlyList<SnapshotGroup> nextGroups,IReadOnlyDictionary<string,SnapshotSplice> changes,CancellationToken cancellation)=>sessionReader.Execute<IReadOnlyDictionary<string,IReadOnlyList<RangeEdit>?>>(c=>
    {
        if(previous.RootId!=next.RootId||previous.Epoch!=next.Epoch)throw new ArgumentException("仅能比较同一根目录的结果。");
        var oldStarts=previousGroups.ToDictionary(g=>g.Id,g=>g.Start);var newStarts=nextGroups.ToDictionary(g=>g.Id,g=>g.Start);
        var result=new Dictionary<string,IReadOnlyList<RangeEdit>?>();
        using var command=c.CreateCommand();
        command.CommandText="""
            SELECT o.ordinal,n.ordinal FROM ResultItems o
            JOIN ResultItems n ON n.session_id=$next AND n.entry_id=o.entry_id
            AND n.observed_version=o.observed_version
            AND n.snapshot_relative_path=o.snapshot_relative_path
            AND n.snapshot_logical_bytes=o.snapshot_logical_bytes
            AND n.snapshot_allocated_bytes IS o.snapshot_allocated_bytes
            AND n.source_root_id IS o.source_root_id AND n.source_root_path IS o.source_root_path AND n.source_root_epoch IS o.source_root_epoch AND n.snapshot_kind=o.snapshot_kind AND n.group_id IS o.group_id
            WHERE o.session_id=$old AND o.ordinal >= $ofirst AND o.ordinal < $oend
            AND n.ordinal >= $nfirst AND n.ordinal < $nend ORDER BY o.ordinal
            """;
        foreach(var (id,change) in changes)
        {
            cancellation.ThrowIfCancellationRequested();long oldStart=oldStarts.GetValueOrDefault(id),newStart=newStarts.GetValueOrDefault(id);
            if(change.Removed==0||change.Added==0){result[id]=change.Removed==0&&change.Added==0?[]:[new(change.Prefix,change.Removed,change.Added)];continue;}
            int oldPosition=change.Prefix,newPosition=change.Prefix;var edits=new List<RangeEdit>();bool ordered=true;
            command.Parameters.Clear();command.Parameters.AddWithValue("$old",previous.Id);command.Parameters.AddWithValue("$next",next.Id);
            command.Parameters.AddWithValue("$ofirst",oldStart+change.Prefix);command.Parameters.AddWithValue("$oend",oldStart+change.Prefix+change.Removed);
            command.Parameters.AddWithValue("$nfirst",newStart+change.Prefix);command.Parameters.AddWithValue("$nend",newStart+change.Prefix+change.Added);
            using(var reader=command.ExecuteReader())while(reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();int oldIndex=checked((int)(reader.GetInt64(0)-oldStart)),newIndex=checked((int)(reader.GetInt64(1)-newStart));
                if(newIndex<newPosition){ordered=false;break;}
                if(oldIndex>oldPosition||newIndex>newPosition)edits.Add(new(oldPosition,oldIndex-oldPosition,newIndex-newPosition));
                oldPosition=oldIndex+1;newPosition=newIndex+1;
            }
            if(!ordered){result[id]=null;continue;}
            if(oldPosition<change.Prefix+change.Removed||newPosition<change.Prefix+change.Added)
                edits.Add(new(oldPosition,change.Prefix+change.Removed-oldPosition,change.Prefix+change.Added-newPosition));
            result[id]=edits;
        }
        return result;
    },cancellation);

    public Task<IReadOnlyDictionary<string,SnapshotSplice>> CompareSnapshots(ResultHandle previous,ResultHandle next,IReadOnlyList<SnapshotGroup> previousGroups,IReadOnlyList<SnapshotGroup> nextGroups,CancellationToken cancellation)=>sessionReader.Execute<IReadOnlyDictionary<string,SnapshotSplice>>(c=>
    {
        if(previous.RootId!=next.RootId||previous.Epoch!=next.Epoch)throw new ArgumentException("仅能增量更新同一个根目录。");
        var oldGroups=previousGroups.ToDictionary(group=>group.Id);
        var ranges=nextGroups.Count==0&&previousGroups.Count==0
            ?new[]{("",0L,previous.Count,0L,next.Count)}
            :nextGroups.Where(group=>oldGroups.ContainsKey(group.Id)).Select(group=>{var old=oldGroups[group.Id];return(group.Id,old.Start,old.Count,group.Start,group.Count);}).ToArray();
        var result=new Dictionary<string,SnapshotSplice>();
        using var command=c.CreateCommand();
        foreach(var (id,oldStart,oldCount,newStart,newCount) in ranges)
        {
            cancellation.ThrowIfCancellationRequested();long common=Math.Min(oldCount,newCount);
            long Boundary(long first,long last,long shift,bool reverse)
            {
                if(last<first)return 0;
                command.CommandText="SELECT o.ordinal FROM ResultItems o LEFT JOIN ResultItems n ON n.session_id=$next AND n.ordinal=o.ordinal+$shift WHERE o.session_id=$previous AND o.ordinal BETWEEN $first AND $last AND (n.entry_id IS NOT o.entry_id OR n.observed_version IS NOT o.observed_version OR n.snapshot_relative_path IS NOT o.snapshot_relative_path OR n.snapshot_logical_bytes IS NOT o.snapshot_logical_bytes OR n.snapshot_allocated_bytes IS NOT o.snapshot_allocated_bytes OR n.snapshot_kind IS NOT o.snapshot_kind OR n.source_root_id IS NOT o.source_root_id OR n.source_root_path IS NOT o.source_root_path OR n.source_root_epoch IS NOT o.source_root_epoch) ORDER BY o.ordinal "+(reverse?"DESC":"ASC")+" LIMIT 1";
                command.Parameters.Clear();command.Parameters.AddWithValue("$previous",previous.Id);command.Parameters.AddWithValue("$next",next.Id);command.Parameters.AddWithValue("$shift",shift);command.Parameters.AddWithValue("$first",first);command.Parameters.AddWithValue("$last",last);
                return command.ExecuteScalar() is long mismatch?(reverse?last-mismatch:mismatch-first):last-first+1;
            }
            long prefix=Boundary(oldStart,oldStart+common-1,newStart-oldStart,false);
            long suffix=Boundary(oldStart+oldCount-(common-prefix),oldStart+oldCount-1,newStart+newCount-oldStart-oldCount,true);
            result[id]=new(checked((int)prefix),checked((int)(oldCount-prefix-suffix)),checked((int)(newCount-prefix-suffix)));
        }
        return result;
    },cancellation);

    public Task<IReadOnlyList<SnapshotItem>> ReadSnapshotEntries(string sessionId,IReadOnlyList<string> entries,CancellationToken cancellation)=>sessionReader.Execute<IReadOnlyList<SnapshotItem>>(c=>
    {
        if(entries.Count>8192)throw new ArgumentOutOfRangeException(nameof(entries));
        using var command=c.CreateCommand();command.CommandText="SELECT i.ordinal,i.entry_id,i.observed_version,i.snapshot_relative_path,i.directory_id,i.snapshot_logical_bytes,i.snapshot_allocated_bytes,i.snapshot_kind,g.group_id,g.relative_path,g.logical_bytes,g.match_count,g.start_ordinal,g.item_count,g.scan_state,g.capacity_scope,i.source_root_id,i.source_root_path,i.source_root_epoch FROM ResultItems i LEFT JOIN ResultGroups g ON g.session_id=i.session_id AND g.group_id=i.group_id WHERE i.session_id=$session AND i.entry_id IN(SELECT value FROM json_each($ids))";
        command.Parameters.AddWithValue("$session",sessionId);command.Parameters.AddWithValue("$ids",System.Text.Json.JsonSerializer.Serialize(entries));
        using var rows=command.ExecuteReader();var found=new List<SnapshotItem>();
        while(rows.Read()){cancellation.ThrowIfCancellationRequested();found.Add(new(rows.GetInt64(0),rows.GetString(1),rows.GetInt64(2),rows.GetString(3),rows.GetString(4),rows.GetInt64(5),rows.IsDBNull(6)?null:rows.GetInt64(6),rows.GetString(7)){Group=rows.IsDBNull(8)?null:ReadGroup(rows,8),SourceRootId=rows.IsDBNull(16)?null:rows.GetString(16),SourceRootPath=rows.IsDBNull(17)?null:rows.GetString(17),SourceRootEpoch=rows.IsDBNull(18)?null:rows.GetInt64(18)});}
        return found;
    },cancellation);
}
