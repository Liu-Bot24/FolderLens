using System.Runtime.CompilerServices;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

/// <summary>On-demand file-size enumeration, independent of the disposable browse index.</summary>
public static class PhysicalCapacityScanner
{
    public static async Task<CapacityReport> Scan(string root,IReadOnlyList<ExclusionSpec> exclusions,
        IProgress<CapacityReport>? progress,CancellationToken cancellation,string? workerExecutable=null)
    {
        root=PathRules.ValidateSource(root);
        bool isolated=root.StartsWith(@"\\",StringComparison.Ordinal)||
            new DriveInfo(Path.GetPathRoot(root)!).DriveType is not (DriveType.Fixed or DriveType.Ram);
        if(isolated && (workerExecutable??=ScanWorkerClient.FindExecutable()) is null)
            throw new ScanWorkerUnavailableException("目录扫描组件不完整，无法安全统计网络或移动磁盘。");
        await using var worker=isolated?new ScanWorkerClient(workerExecutable!):null;
        var rows=new Dictionary<string,DirectoryCapacity>(StringComparer.Ordinal){[""]=new("",0,0,0,0)};
        var pending=new Queue<string>();pending.Enqueue("");
        long errors=0,revision=0;var lastReport=DateTimeOffset.MinValue;
        bool Skip(string path)=>exclusions.Any(e=>e.Mode=="skipScan"&&
            (e.RelativePath.Length==0||PathRules.IsWithinRelative(path,e.RelativePath.Replace('/','\\').TrimEnd('\\'))));
        CapacityReport Snapshot(string state)=>new(root,state,Capacity.BuildDirectories(rows.Values,cancellation))
        {Revision=++revision,Pending=pending.Count,Unresolvable=errors};
        while(pending.Count>0)
        {
            cancellation.ThrowIfCancellationRequested();string relative=pending.Dequeue();
            if(relative.Length>0&&Skip(relative))continue;
            string path=relative.Length==0?root:Path.Combine(root,relative);
            long count=0,logical=0,allocated=0,unknown=0;bool complete=false;
            try
            {
                var packets=worker is null?ReadLocal(path,cancellation):worker.Read(path,false,cancellation);
                await foreach(var packet in packets.WithCancellation(cancellation).ConfigureAwait(false))
                {
                    if(packet.State=="batch")foreach(var entry in packet.Entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string child=relative.Length==0?entry.Name:Path.Combine(relative,entry.Name);
                        if(entry.Directory)
                        {
                            if(entry.SkipReason is null&&!Skip(child)){rows.TryAdd(child,new(child,0,0,0,0));pending.Enqueue(child);}
                            else if(entry.SkipReason=="DeferredOffline")errors++;
                        }
                        else if(entry.SkipReason is null)
                        {
                            checked{count++;logical+=entry.Bytes;allocated+=entry.Allocated??0;if(entry.Allocated is null)unknown++;}
                        }
                    }
                    else if(packet.State=="completed")
                    {complete=true;if(packet.ErrorCode is not null)errors++;}
                    rows[relative]=new(relative,count,logical,allocated,unknown);
                    if(DateTimeOffset.UtcNow-lastReport>=TimeSpan.FromSeconds(1))
                    {progress?.Report(Snapshot("scanning"));lastReport=DateTimeOffset.UtcNow;}
                }
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
            catch(ScanWorkerUnavailableException)
            {
                // The isolated enumerator is unavailable; repeatedly reopening it
                // for every queued SMB directory would waste minutes with no result.
                errors+=pending.Count;pending.Clear();complete=false;
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or TimeoutException)
            {complete=false;}
            if(!complete)errors++;
            rows[relative]=new(relative,count,logical,allocated,unknown);
            if(DateTimeOffset.UtcNow-lastReport>=TimeSpan.FromSeconds(1))
            {progress?.Report(Snapshot("scanning"));lastReport=DateTimeOffset.UtcNow;}
        }
        var final=Snapshot(errors==0?"ready":"partial");progress?.Report(final);return final;
    }

    private static async IAsyncEnumerable<ScanDirectoryPacket> ReadLocal(string path,
        [EnumeratorCancellation]CancellationToken cancellation)
    {
        using var iterator=ScanDirectoryReader.Read(path).GetEnumerator();
        while(await Task.Run(iterator.MoveNext,cancellation).ConfigureAwait(false))
        {cancellation.ThrowIfCancellationRequested();yield return iterator.Current;}
    }
}
