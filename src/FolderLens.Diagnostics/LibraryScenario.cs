using System.Diagnostics;
using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.Diagnostics;

internal static class LibraryScenario
{
    public static async Task<int> Run(string output,string source,string data,string mediaExe,string scanExe,bool scanProfile=false)
    {
        source=Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        foreach(string owned in new[]{output,data})if(Path.GetFullPath(owned).StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.GetFullPath(owned).Equals(source,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Test output must be outside source.");
        Directory.CreateDirectory(output);using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(10));var token=stop.Token;
        double? enumerationMs=null;long enumeratedFiles=0,enumeratedDirectories=0;
        if(scanProfile)
        {
            var watch=Stopwatch.StartNew();var pending=new Queue<string>();pending.Enqueue(source);
            await Task.Run(()=>
            {
                while(pending.TryDequeue(out var directory))
                {
                    token.ThrowIfCancellationRequested();enumeratedDirectories++;
                    foreach(var packet in ScanDirectoryReader.Read(directory))
                        foreach(var entry in packet.Entries)
                        {
                            if(entry.Directory){if(entry.SkipReason is null)pending.Enqueue(Path.Combine(directory,entry.Name));}
                            else enumeratedFiles++;
                        }
                }
            },token);
            enumerationMs=watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Read-only directory enumeration: {enumeratedFiles} files, {enumeratedDirectories} directories, {enumerationMs:F1}ms.");
        }
        await using var catalog=new CatalogStore(data);await catalog.Initialize(token);
        var existing=await catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="SELECT root_id,root_epoch,scan_state FROM Roots WHERE display_path=$path";q.Parameters.AddWithValue("$path",source);using var r=q.ExecuteReader();return r.Read()?(Id:r.GetString(0),Epoch:r.GetInt64(1),State:r.GetString(2)):(Id:"real-library",Epoch:0L,State:"new");},token);
        long epoch=existing.Epoch==0?await catalog.OpenRoot(existing.Id,source,token):existing.Epoch;string rootId=existing.Id;double? scanMs=null;
        var timer=Stopwatch.StartNew();
        if(existing.State!="ready")
        {
            Console.WriteLine("Completing recursive index of the authorized source; outputs remain local.");
            var indexer=new DirectoryIndexer(catalog,scanExe);
            var report=await indexer.Scan(rootId,source,epoch,true,[],new ScanReporter(),token);scanMs=timer.Elapsed.TotalMilliseconds;
            await File.WriteAllTextAsync(Path.Combine(output,"scan-timing.json"),JsonSerializer.Serialize(new{scanMs,renameLookupMs=indexer.RenameLookupTime.TotalMilliseconds,batchWriteMs=indexer.BatchWriteTime.TotalMilliseconds}),token);
            await File.WriteAllTextAsync(Path.Combine(output,"scan.json"),JsonSerializer.Serialize(report),token);
            if(report.State!="ready")throw new IOException($"Source scan {report.State}; errors={report.Errors}. See scan.json.");
        }
        var filter=new FilterSpec{RootId=rootId,Kinds=["image"],ShowHidden=true};
        timer.Restart();var first=await catalog.ReadFirstPage(filter,token);double firstMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();var handle=await catalog.CreateSnapshot(filter,epoch,1,token);double snapshotMs=timer.Elapsed.TotalMilliseconds;
        if(scanProfile)
        {
            timer.Restart();var grouped=await catalog.CreateSnapshot(filter with{Grouping=new(Enabled:true)},epoch,2,token);double groupedMs=timer.Elapsed.TotalMilliseconds;
            var profile=new{enumerationMs,enumeratedFiles,enumeratedDirectories,scanMs,firstPageMs=firstMs,snapshotMs,groupedSnapshotMs=groupedMs,images=handle.Count,groupedImages=grouped.Count,sourceReadOnly=true,osCache="not cleared; enumeration precedes indexing",ui="NOT_RUN: backend profile"};
            await File.WriteAllTextAsync(Path.Combine(output,"scan-profile.json"),JsonSerializer.Serialize(profile,new JsonSerializerOptions{WriteIndented=true}),token);
            Console.WriteLine(JsonSerializer.Serialize(profile));return 0;
        }
        var flat=await catalog.CreateSnapshot(filter with{Recursive=false},epoch,2,token);
        // Compare real enumeration with recursive candidate indexing, without reading image contents.
        var expected=new HashSet<string>(StringComparer.Ordinal);long directories=0;int maxDepth=0;var queue=new Queue<string>();queue.Enqueue(source);
        while(queue.TryDequeue(out var folder))
        {
            token.ThrowIfCancellationRequested();directories++;int depth=folder==source?0:Path.GetRelativePath(source,folder).Split(Path.DirectorySeparatorChar).Length;maxDepth=Math.Max(maxDepth,depth);
            foreach(var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if((entry.Attributes&FileAttributes.Directory)!=0){if((entry.Attributes&FileAttributes.ReparsePoint)==0)queue.Enqueue(entry.FullName);}
                else if(FileKinds.Candidate(entry.Name)=="image")expected.Add(Path.GetRelativePath(source,entry.FullName));
            }
        }
        var pageTimes=new List<double>();var samples=new List<SnapshotItem>();long inspected=0;
        for(long offset=0;offset<handle.Count;offset+=256)
        {
            timer.Restart();var page=await catalog.ReadPage(handle.Id,offset,256,token);pageTimes.Add(timer.Elapsed.TotalMilliseconds);
            foreach(var item in page){if(!expected.Remove(item.RelativePath))throw new InvalidDataException("Index has duplicate/unexpected source entry.");inspected++;}
            if(offset==0||offset/256%Math.Max(1,(handle.Count/256)/4)==0)samples.AddRange(page.Where(p=>!FileKinds.Raw.Contains(Path.GetExtension(p.RelativePath))).Take(16));
        }
        if(expected.Count!=0||inspected!=handle.Count)throw new InvalidDataException($"Recursive index omitted {expected.Count} images.");
        var thumbnails=new List<object>();var fits=new List<object>();int failures=0;
        await using var worker=new WorkerClient(mediaExe,Path.Combine(output,"worker"),WorkerPriority.Visible);
        foreach(var item in samples.DistinctBy(s=>s.EntryId).Take(64))
        {
            var properties=await catalog.ReadFileProperties(rootId,item.EntryId,item.Version,token)??throw new IOException("Indexed properties disappeared.");
            string path=Path.Combine(source,properties.RelativePath);var context=new RequestContext(rootId,epoch,1,item.Ordinal+1,item.Version,1);
            foreach(string operation in thumbnails.Count<12?new[]{"thumbnail","fit"}:new[]{"thumbnail"})
            {
                timer.Restart();ImageReply? reply=null;
                try{reply=await worker.Request(path,operation,context,operation=="thumbnail"?new(192,144):new(1920,1080),token,new(properties.LogicalBytes,properties.ModifiedUtcTicks));double ms=timer.Elapsed.TotalMilliseconds;var info=new FileInfo(path);if(info.Length!=properties.LogicalBytes||info.LastWriteTimeUtc.Ticks!=properties.ModifiedUtcTicks)throw new IOException("Source version changed");var record=new{item.Ordinal,operation,status="PASS",elapsedMs=ms,bytes=new FileInfo(reply.AssetPath!).Length};if(operation=="thumbnail")thumbnails.Add(record);else fits.Add(record);}
                catch(Exception ex)when(ex is not OperationCanceledException){failures++;var record=new{item.Ordinal,operation,status="FAIL",elapsedMs=timer.Elapsed.TotalMilliseconds,error=ex.Message};if(operation=="thumbnail")thumbnails.Add(record);else fits.Add(record);}
                finally{if(reply is not null)await worker.ReleaseAsset(reply);}
            }
        }
        var result=new{scenario="actual-recursive-library",sourceRoot=source,reusedReadyIndex=existing.State=="ready",scanMs,images=handle.Count,rootOnlyImages=flat.Count,directories,maxDepth,recursiveMembership="PASS",firstPageItems=first.Items.Count,firstPageMs=firstMs,snapshotMs,pages=pageTimes.Count,pageP95Ms=pageTimes.Order().ElementAt((int)Math.Floor((pageTimes.Count-1)*.95)),pageMaxMs=pageTimes.Max(),thumbnails,fits,failures,peakHostWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64,ui="NOT_RUN: command-line scenario",cache=(existing.State=="ready"?"Existing app index":"Fresh app index")+"; OS cache not flushed; worker starts cold",sourceIntegrity="Read-only source APIs; sampled size/mtime unchanged; no full-content hashing"};
        string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});await File.WriteAllTextAsync(Path.Combine(output,"library.json"),json,token);Console.WriteLine(json);return failures==0?0:1;
    }
    private sealed class ScanReporter:IProgress<ScanProgress>{private long next;public void Report(ScanProgress p){if(p.Files>=next){next=p.Files+10000;Console.WriteLine($"Indexed {p.Files} files, {p.Directories} directories, {p.Errors} errors");}}}
}
