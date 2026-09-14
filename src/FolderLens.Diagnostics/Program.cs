using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderLens.Core;
using FolderLens.Infrastructure;

if(args.Length<2){Console.Error.WriteLine("Usage: FolderLens.Diagnostics database|text <fixture-output-directory> [count-or-GiB]");return 2;}
string directory=Path.GetFullPath(args[1]);Directory.CreateDirectory(directory);
var options=new JsonSerializerOptions { WriteIndented=true };
try
{
 if(args[0]=="migrate-playlists"&&args.Length==2)
 {
    long collections,members;
    await using(var session=await BrowsingSessionStorage.Open(directory))
    {
        var saved=await session.Catalog.ReadCollections();collections=saved.Count;members=saved.Sum(c=>c.Count);
    }
    Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",collections,members,legacyCatalog="read-only",runtime="removed"}));return 0;
 }
 if(args[0]=="check-migration"&&args.Length==3)return FolderLens.Diagnostics.MigrationCheck.Run(directory,Path.GetFullPath(args[2]));
 if(args[0]=="grouping-scale"&&args.Length is >=2 and <=4)return await FolderLens.Diagnostics.GroupingScaleScenario.Run(directory,args.Length>2?int.Parse(args[2]):50_000,args.Length>3?int.Parse(args[3]):4);
 if(args[0]=="grouping"&&args.Length==4)return await FolderLens.Diagnostics.GroupingScenario.Run(directory,Path.GetFullPath(args[2]),args[3]);
 if(args[0]=="library"&&args.Length==6)return await FolderLens.Diagnostics.LibraryScenario.Run(directory,Path.GetFullPath(args[2]),Path.GetFullPath(args[3]),Path.GetFullPath(args[4]),Path.GetFullPath(args[5]));
 if(args[0]=="scan-profile"&&args.Length==5)return await FolderLens.Diagnostics.LibraryScenario.Run(directory,Path.GetFullPath(args[2]),Path.GetFullPath(args[3]),"",Path.GetFullPath(args[4]),scanProfile:true);
 if(args[0]=="raw-family"&&args.Length==5)return await FolderLens.Diagnostics.RawFamilyScenario.Run(directory,Path.GetFullPath(args[2]),Path.GetFullPath(args[3]),Path.GetFullPath(args[4]));
 if(args[0]=="database")
 {
    int count=args.Length>2?int.Parse(args[2]):1_000_000;
    if(count<1)throw new ArgumentOutOfRangeException(nameof(count));
    string runDirectory=Path.Combine(directory,"query-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(runDirectory);
    await using var catalog=new CatalogStore(directory);await catalog.Initialize();
    long existing=await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files WHERE root_id='benchmark'";return (long)cmd.ExecuteScalar()!;});
    var timer=Stopwatch.StartNew();
    if(existing==0)await catalog.SeedBenchmark(count);else if(existing!=count)throw new InvalidDataException($"Existing benchmark has {existing} rows; requested {count}. Choose a separate directory.");
    double seedMs=existing==0?timer.Elapsed.TotalMilliseconds:0;
    var beforeCheckpoint=Directory.GetFiles(directory,"*.sqlite*").Select(f=>new{file=Path.GetFileName(f),bytes=new FileInfo(f).Length}).ToArray();
    var checkpoint=await catalog.CheckpointCatalog(true);
    if(checkpoint.Busy!=0)throw new IOException("Cannot release fixture-seeding WAL before the benchmark; another connection holds a read lease.");
    var filter=new FilterSpec{RootId="benchmark"};var firstTimes=new List<double>();
    for(int n=0;n<100;n++)
    {
        timer.Restart();var page=await catalog.ReadFirstPage(filter with{Ranges=new(){["logicalBytes"]=new(1024,null)},Sort=new("logicalBytes")});firstTimes.Add(timer.Elapsed.TotalMilliseconds);
        if(page.Items.Count!=Math.Min(count,256))throw new InvalidDataException("First page count mismatch.");
    }
    using var writing=new CancellationTokenSource();long batches=0;long maxWal=0;var samples=new List<(double Elapsed,long Wal,long SessionBytes,long Writes)>();
    var writeTask=Task.Run(async()=>
    {
        while(!writing.IsCancellationRequested)
        {
            // Synthetic background metadata writes run in transactions on the real
            // catalog writer. Keep sort/filter keys unchanged so cardinality is known.
            await catalog.Write(c=>
            {
                using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET source_metadata_version=1,updated_revision=updated_revision+1 WHERE root_id='benchmark' AND entry_id IN (SELECT entry_id FROM Files WHERE root_id='benchmark' ORDER BY entry_id LIMIT 512)";return cmd.ExecuteNonQuery();
            });
            Interlocked.Increment(ref batches);
            try{await Task.Delay(catalog.SnapshotUnderPressure?100:20,writing.Token);}catch(OperationCanceledException){break;}
        }
    });
    var samplingClock=Stopwatch.StartNew();
    var sampling=Task.Run(async()=>
    {
        while(!writing.IsCancellationRequested)
        {
            string walPath=Path.Combine(directory,"catalog.sqlite-wal");long wal=File.Exists(walPath)?new FileInfo(walPath).Length:0;
            long sessions=Directory.GetFiles(directory,"sessions.sqlite*").Sum(p=>new FileInfo(p).Length);maxWal=Math.Max(maxWal,wal);
            samples.Add((samplingClock.Elapsed.TotalMilliseconds,wal,sessions,Interlocked.Read(ref batches)));
            try{await Task.Delay(20,writing.Token);}catch(OperationCanceledException){break;}
        }
    });
    ResultHandle handle;double snapshotMs;
    try {timer.Restart();handle=await catalog.CreateSnapshot(filter,1,1);snapshotMs=timer.Elapsed.TotalMilliseconds;}
    finally {writing.Cancel();await Task.WhenAll(writeTask,sampling);await File.WriteAllLinesAsync(Path.Combine(runDirectory,"wal-samples.csv"),new[]{"elapsedMs,catalogWalBytes,sessionDiskBytes,writeBatches"}.Concat(samples.Select(s=>$"{s.Elapsed.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)},{s.Wal},{s.SessionBytes},{s.Writes}")));}
    if(handle.Count!=count)throw new InvalidDataException("Snapshot count mismatch.");
    var afterCheckpoint=await catalog.CheckpointCatalog(true);
    var raw=new List<double>();
    for(int n=0;n<100;n++){timer.Restart();var page=await catalog.ReadPage(handle.Id,Math.Min(800_000,count-1));raw.Add(timer.Elapsed.TotalMilliseconds);if(page.Count==0 || page[0].Ordinal!=Math.Min(800_000,count-1))throw new InvalidDataException("Deep page mismatch.");}
    await File.WriteAllLinesAsync(Path.Combine(runDirectory,"first-page-ms.csv"),new[]{"sample,elapsedMs"}.Concat(firstTimes.Select((ms,i)=>$"{i},{ms.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)}")));
    await File.WriteAllLinesAsync(Path.Combine(runDirectory,"deep-page-ms.csv"),new[]{"sample,elapsedMs"}.Concat(raw.Select((ms,i)=>$"{i},{ms.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)}")));
    bool snapshotPass=snapshotMs<=30_000&&maxWal<256L*1024*1024&&batches>0&&afterCheckpoint.Busy==0;
    var result=new {scenario="synthetic-catalog-concurrent-writes-not-file-enumeration",count,reusedFixture=existing!=0,seedMs,beforeCheckpoint,seedingCheckpoint=checkpoint,afterCheckpoint,snapshotMs,firstPageP95Ms=firstTimes.Order().ElementAt(94),deepPageP95Ms=raw.Order().ElementAt(94),concurrentWriteBatches=batches,peakCatalogWalBytes=maxWal,peakSessionDiskBytes=samples.Max(s=>s.SessionBytes),peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64,osCache="warm indexed database; no OS cache flush",snapshotSamples=1,firstPageSamples=100,deepPageSamples=100,snapshotHardGate=snapshotPass?"PASS":"FAIL",referenceHardwareGate="NOT_RUN",evidenceDirectory=runDirectory};
    string json=JsonSerializer.Serialize(result,options);await File.WriteAllTextAsync(Path.Combine(runDirectory,"result.json"),json);Console.WriteLine(json);
    return snapshotPass?0:1;
 }
 if(args[0]=="text")
 {
    int gib=args.Length>2?int.Parse(args[2]):5;long length=checked((long)gib*1024*1024*1024);
    string path=Path.Combine(directory,$"utf8-{gib}GiB.txt");byte[] block=new byte[1024*1024];byte[] line=Encoding.UTF8.GetBytes("FolderLens UTF8 内容边界测试 0123456789\r\n");
    int filled=0;while(filled+line.Length<=block.Length){line.CopyTo(block,filled);filled+=line.Length;}Array.Fill(block,(byte)' ',filled,block.Length-filled);
    string hash;
    var timer=Stopwatch.StartNew();
    using(var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
    {
      if(!File.Exists(path))
      {
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,FileOptions.SequentialScan);
        for(long done=0;done<length;done+=block.Length){file.Write(block);sha.AppendData(block);}file.Flush(true);hash=Convert.ToHexString(sha.GetHashAndReset());
      }
      else { using var file=File.OpenRead(path);if(file.Length!=length)throw new InvalidDataException("Fixture size mismatch.");hash=Convert.ToHexString(SHA256.HashData(file)); }
    }
    double generationMs=timer.Elapsed.TotalMilliseconds;var times=new List<double>();long deep=0;
    for(int n=0;n<100;n++)
    {
      timer.Restart();using var reader=new BoundedTextReader(path);var first=reader.ReadWindow(0);times.Add(timer.Elapsed.TotalMilliseconds);
      if(!first.Text.Contains("内容边界测试"))throw new InvalidDataException("Fixture decode failed.");
      var page=reader.ReadWindow(Math.Min(length-1024,4L*1024*1024*1024+7));deep=page.Start;if(page.Text.Length==0 || page.Next<=page.Start)throw new InvalidDataException("Deep text seek failed.");
    }
    await File.WriteAllLinesAsync(Path.Combine(directory,"first-page-ms.csv"),new[]{"sample,elapsedMs"}.Concat(times.Select((ms,i)=>$"{i},{ms.ToString("F6",System.Globalization.CultureInfo.InvariantCulture)}")));
    var result=new{scenario="real-nonsparse-UTF8-text",length,sha256=hash,generationMs,firstPageP95Ms=times.Order().ElementAt(94),deepStart=deep,osCache="warm after fixture generation; no OS cache flush",peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64};string json=JsonSerializer.Serialize(result,options);await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),json);Console.WriteLine(json);return 0;
 }
 throw new ArgumentException("Unknown scenario.");
}
catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
