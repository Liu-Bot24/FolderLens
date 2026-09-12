using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.App;

internal sealed record SharedRowRange(int OldStart,int NewStart,int Length,SnapshotGroup? Group);

public sealed partial class VirtualResults
{
    private Func<int,FileRow?>? resolveShared;
    private Action<FileRow,int>? shareCreated;
    private FileRow? Alive(int index)=>identities.TryGetValue(index,out var weak)&&weak.TryGetTarget(out var row)?row:null;
    private void ImportShared(FileRow row,int index,SnapshotGroup? group,bool relocate)
    {
        var existing=Alive(index);
        if(existing is not null&&!ReferenceEquals(existing,row))throw new InvalidOperationException("未变化结果位置出现了两个活动文件行。");
        // Visible rows may already have been retained by the caller. Repeatedly
        // removing/re-adding those keys can repeatedly rebuild the weak table.
        if(!positions.TryGetValue(row,out var position)||position.Index!=index)
        {positions.Remove(row);positions.Add(row,new(index));}
        if(existing is null)identities[index]=new(row);
        if(pages.TryGetValue(index/256,out var page))page[index%256]=row;
        if(relocate)row.Relocate(index,group);
    }
    internal IDisposable ShareUnchangedRowsWith(VirtualResults next,IReadOnlyList<SharedRowRange> ranges)=>new IdentityTransition(this,next,ranges);
    // Share only identity during the synchronous publication. Neither data pages nor
    // snapshot leases form a predecessor chain after this scope ends.
    private sealed class IdentityTransition:IDisposable
    {
        private readonly VirtualResults previous,next;
        private readonly SharedRowRange[] byOld,byNew;
        private bool disposed;
        public IdentityTransition(VirtualResults previous,VirtualResults next,IReadOnlyList<SharedRowRange> ranges)
        {
            this.previous=previous;this.next=next;byOld=ranges.OrderBy(r=>r.OldStart).ToArray();byNew=ranges.OrderBy(r=>r.NewStart).ToArray();
            if(previous.resolveShared is not null||next.resolveShared is not null)throw new InvalidOperationException("结果发布不能重入。");
            previous.resolveShared=index=>Find(index,false) is {} r?next.Alive(r.NewStart+index-r.OldStart):null;
            next.resolveShared=index=>Find(index,true) is {} r?previous.Alive(r.OldStart+index-r.NewStart):null;
            previous.shareCreated=(row,index)=>{if(Find(index,false) is {} r)next.ImportShared(row,r.NewStart+index-r.OldStart,r.Group,true);};
            next.shareCreated=(row,index)=>{if(Find(index,true) is {} r)previous.ImportShared(row,r.OldStart+index-r.NewStart,null,false);};
            try
            {
                foreach(var row in previous.CachedRows().ToArray())
                {
                    int index=previous.IndexOf(row);
                    if(Find(index,false) is {} r)next.ImportShared(row,r.NewStart+index-r.OldStart,r.Group,true);
                }
            }
            catch{Dispose();throw;}
        }
        private SharedRowRange? Find(int index,bool final)
        {
            var ranges=final?byNew:byOld;int low=0,high=ranges.Length-1;
            while(low<=high)
            {
                int mid=low+(high-low)/2;var range=ranges[mid];int start=final?range.NewStart:range.OldStart;
                if(index<start)high=mid-1;else if(index-start>=range.Length)low=mid+1;else return range;
            }
            return null;
        }
        public void Dispose(){if(disposed)return;disposed=true;previous.resolveShared=null;previous.shareCreated=null;next.resolveShared=null;next.shareCreated=null;}
    }
    internal static IReadOnlyList<SharedRowRange> SharedRanges(int oldCount,int newCount,IReadOnlyList<SnapshotGroup> oldGroups,IReadOnlyList<SnapshotGroup> newGroups,IReadOnlyDictionary<string,IReadOnlyList<RangeEdit>> changes)
    {
        var result=new List<SharedRowRange>();var previous=oldGroups.ToDictionary(g=>g.Id);
        void Add(int oldStart,int newStart,int oldLength,int newLength,SnapshotGroup? group,IReadOnlyList<RangeEdit> edits)
        {
            int old=0,current=0;
            foreach(var edit in edits)
            {
                int same=edit.Prefix-old;
                if(same<0||edit.Removed<0||edit.Added<0||edit.Prefix+edit.Removed>oldLength)throw new InvalidDataException("结果差分区间无效。");
                if(same>0)result.Add(new(oldStart+old,newStart+current,same,group));
                old=edit.Prefix+edit.Removed;current+=same+edit.Added;
            }
            int tail=oldLength-old;if(current+tail!=newLength)throw new InvalidDataException("结果差分计数不一致。");
            if(tail>0)result.Add(new(oldStart+old,newStart+current,tail,group));
        }
        if(oldGroups.Count==0&&newGroups.Count==0)Add(0,0,oldCount,newCount,null,changes[""]);
        else foreach(var group in newGroups)if(previous.TryGetValue(group.Id,out var old))Add(checked((int)old.Start),checked((int)group.Start),checked((int)old.Count),checked((int)group.Count),group,changes[group.Id]);
        return result;
    }
}
