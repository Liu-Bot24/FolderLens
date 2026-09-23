namespace FolderLens.Core;

public sealed record CapacityFile(string RelativePath,long LogicalBytes,long? AllocatedBytes);
public sealed record DirectoryCapacity(string RelativePath,long Files,long LogicalBytes,long AllocatedKnown,long AllocationUnknown);
public sealed record CapacityRow(string RelativePath,long DirectFiles,long SubtreeFiles,long DirectLogical,long SubtreeLogical,long DirectAllocatedKnown,long SubtreeAllocatedKnown,long AllocationUnknown);
public sealed record CapacityViewRow(string RelativePath,string Label,long Files,long KnownBytes,long UnknownCount,double? Fraction,bool IsDirectFiles);

public static class Capacity
{
    private sealed class Accumulator
    {
        public long DirectFiles,Files,DirectLogical,Logical,DirectAllocated,Allocated,Unknown;
    }
    public static IReadOnlyList<CapacityRow> Build(IEnumerable<CapacityFile> files,IEnumerable<string>? emptyDirectories=null,CancellationToken cancellation=default)
    {
        var rows=new Dictionary<string,Accumulator>(StringComparer.Ordinal){[""]=new()};
        if(emptyDirectories is not null)foreach(string directory in emptyDirectories){cancellation.ThrowIfCancellationRequested();rows.TryAdd(directory,new());}
        foreach(var file in files)
        {
            cancellation.ThrowIfCancellationRequested();if(file.LogicalBytes<0 || file.AllocatedBytes<0)throw new ArgumentOutOfRangeException(nameof(files));
            string directory=Path.GetDirectoryName(file.RelativePath)??"";
            if(!rows.TryGetValue(directory,out var direct))rows[directory]=direct=new();
            checked{direct.DirectFiles++;direct.Files++;direct.DirectLogical+=file.LogicalBytes;direct.Logical+=file.LogicalBytes;direct.DirectAllocated+=file.AllocatedBytes??0;direct.Allocated+=file.AllocatedBytes??0;if(file.AllocatedBytes is null)direct.Unknown++;}
        }
        return Rollup(rows,cancellation);
    }
    public static IReadOnlyList<CapacityRow> BuildDirectories(IEnumerable<DirectoryCapacity> directories,CancellationToken cancellation=default)
    {
        var rows=new Dictionary<string,Accumulator>(StringComparer.Ordinal){[""]=new()};
        foreach(var directory in directories)
        {
            cancellation.ThrowIfCancellationRequested();
            if(directory.Files<0||directory.LogicalBytes<0||directory.AllocatedKnown<0||directory.AllocationUnknown<0||directory.AllocationUnknown>directory.Files)throw new ArgumentOutOfRangeException(nameof(directories));
            rows[directory.RelativePath]=new(){DirectFiles=directory.Files,Files=directory.Files,DirectLogical=directory.LogicalBytes,Logical=directory.LogicalBytes,DirectAllocated=directory.AllocatedKnown,Allocated=directory.AllocatedKnown,Unknown=directory.AllocationUnknown};
        }
        return Rollup(rows,cancellation);
    }
    private static IReadOnlyList<CapacityRow> Rollup(Dictionary<string,Accumulator> rows,CancellationToken cancellation)
    {
        // Visit ancestors once per directory, not once per file. Include parents
        // of empty directories so they remain navigable in a complete hierarchy.
        foreach(string path in rows.Keys.ToArray())
        {
            string directory=path;
            while(directory.Length>0)
            {
                cancellation.ThrowIfCancellationRequested();directory=Path.GetDirectoryName(directory)??"";
                if(!rows.TryAdd(directory,new()))break;
            }
        }
        foreach(string directory in rows.Keys.OrderByDescending(p=>p.Length))
        {
            cancellation.ThrowIfCancellationRequested();if(directory.Length==0)continue;
            var child=rows[directory];var parent=rows[Path.GetDirectoryName(directory)??""];
            checked{parent.Files+=child.Files;parent.Logical+=child.Logical;parent.Allocated+=child.Allocated;parent.Unknown+=child.Unknown;}
        }
        return rows.Select(p=>new CapacityRow(p.Key,p.Value.DirectFiles,p.Value.Files,p.Value.DirectLogical,p.Value.Logical,p.Value.DirectAllocated,p.Value.Allocated,p.Value.Unknown)).ToArray();
    }
    public static IReadOnlyList<CapacityViewRow> Shares(IReadOnlyList<CapacityRow> rows,string directory,bool allocated,int maximum=20)
    {
        if(maximum is <1 or >20)throw new ArgumentOutOfRangeException(nameof(maximum));
        var level=CurrentLevel(rows,directory,allocated);if(level.Count<=maximum)return level;
        var visible=level.Take(maximum).ToList();var rest=level.Skip(maximum).ToArray();
        long bytes=0,files=0,unknown=0;checked{foreach(var row in rest){bytes+=row.KnownBytes;files+=row.Files;unknown+=row.UnknownCount;}}
        double? fraction=rest.All(r=>r.Fraction is null)?null:rest.Sum(r=>r.Fraction??0);
        visible.Add(new("",$"其他（{rest.Length:N0} 项）",files,bytes,unknown,fraction,true));return visible;
    }
    public static IReadOnlyList<CapacityViewRow> RankAtDepth(IReadOnlyList<CapacityRow> rows,string directory,int depth)
    {
        if(depth<1)throw new ArgumentOutOfRangeException(nameof(depth));
        var parent=rows.FirstOrDefault(r=>r.RelativePath==directory);if(parent is null)return [];
        string prefix=directory.Length==0?"":directory+Path.DirectorySeparatorChar;
        var descendants=rows.Where(r=>r.RelativePath!=directory&&
            (directory.Length==0||PathRules.IsWithinRelative(r.RelativePath,directory)))
            .Select(r=>(Row:r,Label:r.RelativePath[prefix.Length..]))
            .ToArray();
        var branches=descendants.Select(r=>Path.GetDirectoryName(r.Row.RelativePath)??"").ToHashSet(StringComparer.Ordinal);
        long total=parent.SubtreeLogical;
        return descendants.Where(item=>
        {
            int level=1+item.Label.Count(ch=>ch is '\\' or '/');
            return level==depth||level<depth&&!branches.Contains(item.Row.RelativePath);
        }).Select(item=>new CapacityViewRow(item.Row.RelativePath,item.Label,item.Row.SubtreeFiles,
            item.Row.SubtreeLogical,0,total==0?null:(double)item.Row.SubtreeLogical/total,false))
            .OrderByDescending(row=>row.KnownBytes).ThenBy(row=>row.RelativePath,StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<CapacityViewRow> SharesAtDepth(IReadOnlyList<CapacityRow> rows,string directory,int depth,int maximum=20)
    {
        if(maximum is <1 or >20)throw new ArgumentOutOfRangeException(nameof(maximum));
        var parent=rows.FirstOrDefault(r=>r.RelativePath==directory);if(parent is null)return [];
        var ranked=RankAtDepth(rows,directory,depth);
        var visible=ranked.Take(maximum).ToList();
        long included=0,files=0;
        checked{foreach(var row in ranked){included+=row.KnownBytes;files+=row.Files;}}
        if(ranked.Count>maximum)
        {
            var rest=ranked.Skip(maximum).ToArray();
            long bytes=0,remainingFiles=0;checked{foreach(var row in rest){bytes+=row.KnownBytes;remainingFiles+=row.Files;}}
            visible.Add(new("",$"其他子文件夹（{rest.Length:N0} 项）",remainingFiles,bytes,0,
                parent.SubtreeLogical==0?null:(double)bytes/parent.SubtreeLogical,true));
        }
        long direct=checked(parent.SubtreeLogical-included),directFiles=checked(parent.SubtreeFiles-files);
        if(direct>0||directFiles>0)
            visible.Add(new(directory,"未列入排名的直属文件",directFiles,direct,0,
                parent.SubtreeLogical==0?null:(double)direct/parent.SubtreeLogical,true));
        return visible;
    }
    public static IReadOnlyList<CapacityViewRow> CurrentLevel(IReadOnlyList<CapacityRow> rows,string directory,bool allocated)
    {
        var parent=rows.FirstOrDefault(r=>r.RelativePath==directory);if(parent is null)return [];
        long total=allocated?parent.SubtreeAllocatedKnown:parent.SubtreeLogical;
        var result=rows.Where(r=>r.RelativePath!=directory && (Path.GetDirectoryName(r.RelativePath)??"")==directory)
            .Select(r=>new CapacityViewRow(r.RelativePath,Path.GetFileName(r.RelativePath),r.SubtreeFiles,allocated?r.SubtreeAllocatedKnown:r.SubtreeLogical,allocated?r.AllocationUnknown:0,total==0?null:(double)(allocated?r.SubtreeAllocatedKnown:r.SubtreeLogical)/total,false)).ToList();
        result.Add(new(directory,"本目录直接文件",parent.DirectFiles,allocated?parent.DirectAllocatedKnown:parent.DirectLogical,allocated?Math.Max(0,parent.AllocationUnknown-rows.Where(r=>(Path.GetDirectoryName(r.RelativePath)??"")==directory&&r.RelativePath!=directory).Sum(r=>r.AllocationUnknown)):0,total==0?null:(double)(allocated?parent.DirectAllocatedKnown:parent.DirectLogical)/total,true));
        return result.OrderByDescending(r=>r.KnownBytes).ThenBy(r=>r.RelativePath,StringComparer.Ordinal).ToArray();
    }
}
