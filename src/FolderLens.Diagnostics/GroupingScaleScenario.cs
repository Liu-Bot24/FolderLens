using System.Diagnostics;
using System.Text.Json;
using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.Diagnostics;

// Synthetic index only: no media files, source scanning, or application window.
internal static class GroupingScaleScenario
{
    public static async Task<int> Run(string output,int directoryCount,int fanout)
    {
        if(directoryCount is <10 or >100_000||fanout is <2 or >100_000)throw new ArgumentOutOfRangeException(nameof(directoryCount));
        string data=Path.Combine(output,"index");
        if(Directory.Exists(data))throw new IOException("Choose a fresh grouping-scale output directory.");
        var paths=new string[directoryCount+1];paths[0]="";
        var parent=new int[directoryCount+1];var owner=new int[directoryCount+1];
        var children=Enumerable.Range(0,directoryCount+1).Select(_=>new List<int>()).ToArray();
        var totals=new long[directoryCount+1];int maxDepth=0;
        for(int id=1;id<=directoryCount;id++)
        {
            int p=(id-1)/fanout;parent[id]=p;children[p].Add(id);owner[id]=p==0?id:owner[p];
            paths[id]=(p==0?"":paths[p]+"\\")+$"folder{id:D6}";
            maxDepth=Math.Max(maxDepth,paths[id].Count(c=>c=='\\')+1);
            totals[id]=Size(id*2-1)+Size(id*2);
        }
        for(int id=directoryCount;id>0;id--)totals[parent[id]]=checked(totals[parent[id]]+totals[id]);
        foreach(var list in children)list.Sort((a,b)=>{int order=totals[b].CompareTo(totals[a]);return order!=0?order:a.CompareTo(b);});
        await using var catalog=new CatalogStore(data);await catalog.Initialize();
        var watch=Stopwatch.StartNew();await catalog.SeedBenchmark(directoryCount*2);
        await catalog.Write(c=>
        {
            using var transaction=c.BeginTransaction();
            using var insert=c.CreateCommand();insert.Transaction=transaction;
            insert.CommandText="INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode) VALUES($id,'benchmark',$parent,$name,$path,$path,'sensitive')";
            foreach(string name in new[]{"$id","$parent","$name","$path"})insert.Parameters.AddWithValue(name,"");
            using var update=c.CreateCommand();update.Transaction=transaction;
            update.CommandText="UPDATE Files SET directory_id=$dir,relative_path=$path,canonical_key=$path,path_sort_key=$key,logical_bytes=$size WHERE entry_id=$id";
            foreach(string name in new[]{"$dir","$path","$key","$size","$id"})update.Parameters.AddWithValue(name,"");
            for(int id=1;id<=directoryCount;id++)
            {
                insert.Parameters["$id"].Value=$"scale-{id}";insert.Parameters["$parent"].Value=parent[id]==0?"benchmark-dir":$"scale-{parent[id]}";
                insert.Parameters["$name"].Value=$"folder{id:D6}";insert.Parameters["$path"].Value=paths[id];insert.ExecuteNonQuery();
                foreach(int file in new[]{id*2-1,id*2})
                {
                    string path=paths[id]+$"\\file{file}.jpg";
                    update.Parameters["$dir"].Value=$"scale-{id}";update.Parameters["$path"].Value=path;update.Parameters["$key"].Value=NaturalOrder.Key(path);
                    update.Parameters["$size"].Value=Size(file);update.Parameters["$id"].Value=file.ToString("D12");update.ExecuteNonQuery();
                }
            }
            transaction.Commit();return true;
        });
        double seedMs=watch.Elapsed.TotalMilliseconds;await catalog.CheckpointCatalog(true);
        var preorder=new List<int>(directoryCount);var stack=new Stack<int>(children[0].AsEnumerable().Reverse());
        while(stack.TryPop(out int id)){preorder.Add(id);foreach(int child in children[id].AsEnumerable().Reverse())stack.Push(child);}
        var all=preorder.SelectMany(id=>new[]{id*2-1,id*2}.OrderByDescending(Size)).ToArray();
        // Sizes are unique, so the reference does not reuse production sort/group SQL.
        var firstGroups=Enumerable.Range(1,directoryCount*2).ToLookup(file=>owner[(file+1)/2]);
        var first=children[0].SelectMany(top=>firstGroups[top].OrderByDescending(Size)).ToArray();
        var plain=Enumerable.Range(1,directoryCount*2).OrderByDescending(Size).ToArray();
        var runs=new List<object>();
        foreach(var (mode,expected) in new[]{("off",plain),("first",first),("all",all)})
        {
            var filter=new FilterSpec{RootId="benchmark",Kinds=["image"],Sort=new("logicalBytes","desc"),Grouping=new(mode!="off",mode=="off"?"all":mode)};
            watch.Restart();var handle=await catalog.CreateSnapshot(filter,1,runs.Count+1);double snapshotMs=watch.Elapsed.TotalMilliseconds;
            if(handle.Count!=expected.Length)throw new InvalidDataException($"{mode}: wrong result count.");
            watch.Restart();var groups=await catalog.ReadGroups(handle.Id);double groupReadMs=watch.Elapsed.TotalMilliseconds;
            int expectedGroups=mode=="all"?directoryCount:mode=="first"?children[0].Count:0;
            if(groups.Count!=expectedGroups||mode!="off"&&groups.Sum(g=>g.Count)!=handle.Count)throw new InvalidDataException($"{mode}: wrong group coverage.");
            watch.Restart();var last=await catalog.ReadPage(handle.Id,Math.Max(0,handle.Count-256));double lastPageMs=watch.Elapsed.TotalMilliseconds;
            if(last.Count!=Math.Min(256,handle.Count))throw new InvalidDataException("Incomplete deep page.");
            watch.Restart();
            for(int offset=0;offset<expected.Length;offset+=256)
            {
                var page=await catalog.ReadPage(handle.Id,offset);
                if(page.Count!=Math.Min(256,expected.Length-offset))throw new InvalidDataException("Incomplete page.");
                for(int index=0;index<page.Count;index++)
                    if(page[index].EntryId!=expected[offset+index].ToString("D12"))throw new InvalidDataException($"{mode}: wrong order at {offset+index}.");
            }
            double verifyMs=watch.Elapsed.TotalMilliseconds;
            long groupEstimate=groups.Sum(g=>256+g.RelativePath.Length*2L+g.Id.Length*2L);
            runs.Add(new{mode,files=handle.Count,groups=groups.Count,snapshotMs,groupReadMs,lastPageMs,verifyMs,order="PASS",groupEstimateBytes=groupEstimate,processPeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64});
            Console.WriteLine($"{mode}: {handle.Count} files, {groups.Count} groups, snapshot {snapshotMs:F0} ms, groups {groupReadMs:F0} ms, last page {lastPageMs:F2} ms; full order PASS");
            await catalog.ReleaseSnapshot(handle.Id);
        }
        await File.WriteAllTextAsync(Path.Combine(output,"grouping-scale.json"),JsonSerializer.Serialize(new{scenario="synthetic index; no source media accessed",directoryCount,fanout,maxDepth,seedMs,runs,ui="NOT_RUN",memoryScope="whole diagnostics process including fixture arrays and reference order; not application UI",samplesPerMode=1},new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
    private static long Size(int file)=>checked(((long)file*7919%1_000_003+1)*1024);
}
