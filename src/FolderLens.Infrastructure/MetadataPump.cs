using System.ComponentModel;
using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

/// <summary>One sequential metadata producer with a bounded, keyset-paged candidate list.</summary>
public sealed class MetadataPump(CatalogStore catalog,WorkerClient worker,MediaTools? media=null,string? scanExecutable=null)
{
    private readonly SemaphoreSlim runGate=new(1,1);
    public Task FillGeometry(string rootId,string root,long epoch,IProgress<long>? progress,CancellationToken cancellation)=>Fill(rootId,root,epoch,progress,false,cancellation);
    public Task FillAll(string rootId,string root,long epoch,IProgress<long>? progress,CancellationToken cancellation,string? collectionId=null)=>Fill(rootId,root,epoch,progress,true,cancellation,collectionId);
    private async Task Fill(string rootId,string root,long epoch,IProgress<long>? progress,bool includeMedia,CancellationToken cancellation,string? collectionId=null)
    {
        if(includeMedia && media is null)throw new InvalidOperationException("视频与音频信息组件尚未配置。");
        await runGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await using var sourceProbe=new SourceFileProbe(scanExecutable);
            long completed=0;string after="";
            while(true)
            {
                // Relinquish the writer while a long read lease approaches its WAL/time
                // budget. No source file or candidate task is held during this delay.
                while(catalog.SnapshotUnderPressure)await Task.Delay(100,cancellation).ConfigureAwait(false);
                var batch=await catalog.Read(c=>
                {
                    using var cmd=c.CreateCommand();cmd.CommandText="""
                        SELECT entry_id,relative_path,file_version,kind,logical_bytes,mtime_utc_ticks,root_id,
                            (SELECT display_path FROM Roots r WHERE r.root_id=f.root_id),(SELECT root_epoch FROM Roots r WHERE r.root_id=f.root_id)
                        FROM Files f WHERE root_id=$root
                        AND entry_id>$after AND entry_state='present' AND hydration_state='local'
                        AND EXISTS(SELECT 1 FROM Roots r WHERE r.root_id=f.root_id AND r.root_epoch=$epoch)
                        AND (kind='image' OR ($media=1 AND kind IN ('video','audio')))
                        AND (
                            (kind='image' AND (NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='imageGeometry' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))
                                OR NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='identity' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))
                                OR NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='animation' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))
                                OR NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='captureTime' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))
                                OR NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='imageColor' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))))
                            OR (kind IN ('video','audio') AND (NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='media' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))
                                OR NOT EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='captureTime' AND fs.source_version=f.file_version AND (fs.state IN ('ready','unsupported') OR (fs.state='failed' AND fs.retry_after_utc_ticks>$now)))))
                        ) ORDER BY entry_id LIMIT 256
                        """;
                    cmd.Parameters.AddWithValue("$root",rootId);cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$after",after);cmd.Parameters.AddWithValue("$media",includeMedia?1:0);cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
                    cmd.Parameters.AddWithValue("$collection",collectionId??(object)DBNull.Value);
                    if(collectionId is not null)cmd.CommandText=cmd.CommandText.Replace("WHERE root_id=$root","WHERE location_key IN(SELECT location_key FROM CollectionMembers WHERE collection_id=$collection)").Replace("AND r.root_epoch=$epoch","");
                    using var rows=cmd.ExecuteReader();var entries=new List<(string Id,string Path,long Version,string Kind,long Length,long Modified,string RootId,string Root,long Epoch)>();
                    while(rows.Read())entries.Add((rows.GetString(0),rows.GetString(1),rows.GetInt64(2),rows.GetString(3),rows.GetInt64(4),rows.GetInt64(5),rows.GetString(6),rows.GetString(7),rows.GetInt64(8)));return entries;
                },cancellation).ConfigureAwait(false);
                if(batch.Count==0)break;
                // Observe a bounded batch together. The 300 ms stability interval is
                // shared by the batch, rather than adding 300 ms to every indexed file.
                var observations=new Dictionary<string,Observation>();
                foreach(var entry in batch)observations.Add(entry.Id,await Observe(sourceProbe,collectionId is null?root:entry.Root,entry.Path,cancellation).ConfigureAwait(false));
                foreach(var entry in batch)
                {
                    cancellation.ThrowIfCancellationRequested();after=entry.Id;
                    while(catalog.SnapshotUnderPressure)await Task.Delay(100,cancellation).ConfigureAwait(false);
                    try
                    {
                        var observation=observations[entry.Id];if(observation.Error is {} error)throw error;
                        string path=observation.Path;var before=observation.Stat;
                        if(before.Length!=entry.Length || before.ModifiedUtcTicks!=entry.Modified)throw new IOException("FileChanged");
                        // This also avoids racing common download/copy-in-progress files.
                        double remaining=300-System.Diagnostics.Stopwatch.GetElapsedTime(observation.Timestamp).TotalMilliseconds;
                        if(remaining>0)await Task.Delay(TimeSpan.FromMilliseconds(remaining),cancellation).ConfigureAwait(false);
                        if(await sourceProbe.Read(path,cancellation).ConfigureAwait(false)!=before)throw new IOException("FileChanged");
                        if(entry.Kind=="image")
                        {
                            var reply=await worker.Request(path,"probe",new(entry.RootId,entry.Epoch,1,1,entry.Version,1),new(),cancellation,before).ConfigureAwait(false);
                            var metadata=reply.Message.Metadata?.ValueKind==System.Text.Json.JsonValueKind.Object?reply.Message.Metadata.Value:throw new InvalidDataException("ProbeFailed");
                            if(await sourceProbe.Read(path,cancellation).ConfigureAwait(false)!=before)throw new IOException("FileChanged");
                            await catalog.ApplyImageMetadata(entry.Id,entry.Version,entry.RootId,entry.Epoch,metadata.GetProperty("width").GetInt32(),metadata.GetProperty("height").GetInt32(),metadata.GetProperty("format").GetString()!,metadata.GetProperty("isRaw").GetBoolean(),metadata.GetProperty("isAnimated").GetBoolean(),metadata.GetProperty("provider").GetString()!,cancellation,metadata.GetProperty("pages").GetInt32()).ConfigureAwait(false);
                            var details=metadata.GetProperty("details").Deserialize<ContentMetadataDetails>(WorkerProtocol.Json)??throw new InvalidDataException("MetadataDetailsMissing");
                            await catalog.ApplyFileDetails(entry.Id,entry.Version,entry.RootId,entry.Epoch,details,metadata.GetProperty("provider").GetString()!,cancellation).ConfigureAwait(false);
                        }
                        else
                        {
                            var metadata=await media!.Probe(path,cancellation).ConfigureAwait(false);
                            if(await sourceProbe.Read(path,cancellation).ConfigureAwait(false)!=before)throw new IOException("FileChanged");
                            await catalog.ApplyMediaMetadata(entry.Id,entry.Version,entry.RootId,entry.Epoch,metadata,"ffprobe-v1",cancellation).ConfigureAwait(false);
                            if(metadata.Details is {} details)await catalog.ApplyFileDetails(entry.Id,entry.Version,entry.RootId,entry.Epoch,details,"ffprobe-v1",cancellation).ConfigureAwait(false);
                        }
                    }
                    catch(Win32Exception ex) when(ex.NativeErrorCode is 2 or 3 or 126 or 127 or 193)
                    {
                        // Engine startup failure is global; do not mark every file bad.
                        throw new InvalidOperationException("媒体探测组件无法启动，请检查组件安装。",ex);
                    }
                    catch(Exception ex) when(ex is not OperationCanceledException)
                    {
                        string error=ex is TimeoutException or InvalidDataException {Message:"Timeout"}?"Timeout":ex is UnauthorizedAccessException?"AccessDenied":ex is IOException {Message:"FileChanged"} or InvalidDataException {Message:"FileChanged"}?"FileChanged":ex is FileNotFoundException?"NotFound":ex is InvalidDataException {Message:"UnsupportedCodec"}?"UnsupportedCodec":"ProbeFailed";
                        await catalog.MarkMetadataFailure(entry.Id,entry.Version,entry.RootId,entry.Epoch,entry.Kind=="image"?["identity","imageGeometry","animation","captureTime","imageColor"]:["identity","media","imageGeometry","captureTime"],error,error=="UnsupportedCodec",cancellation).ConfigureAwait(false);
                    }
                    progress?.Report(++completed);
                }
            }
        }
        finally {runGate.Release();}
    }
    private sealed record Observation(string Path,SourceFileStamp Stat,long Timestamp,Exception? Error);
    private static async Task<Observation> Observe(SourceFileProbe probe,string root,string relative,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try{string path=PathRules.ValidateSource(Path.GetFullPath(Path.Combine(root,relative)));return new(path,await probe.Read(path,cancellation).ConfigureAwait(false),System.Diagnostics.Stopwatch.GetTimestamp(),null);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or TimeoutException){return new("",default,0,ex);}
    }
}
