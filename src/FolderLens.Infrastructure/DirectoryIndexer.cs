using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed record ScanProgress(long Files,long Directories,long Errors,string State);
public sealed class RootIdentityChangedException():IOException("根目录的卷或创建身份已改变；保留旧索引，请以新的根上下文打开。");

/// <summary>Disk-backed depth-first traversal reaches nested content early; only a completed directory can reconcile unseen entries.</summary>
public sealed class DirectoryIndexer(CatalogStore catalog,string? scanWorkerExecutable=null)
{
    public TimeSpan RenameLookupTime { get; private set; }
    public TimeSpan BatchWriteTime { get; private set; }
    public static string StablePathId(string rootId,string relative)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootId+"\0"+relative)));
    public async Task<ScanProgress> Scan(string rootId,string root,long epoch,bool recursive,ExclusionSpec[] exclusions,
        IProgress<ScanProgress>? progress,CancellationToken cancellation,bool forceRefresh=false,string? scopeRelative=null,bool descendants=true)
    {
        root=PathRules.ValidateSource(root);
        if(scopeRelative is not null&&(Path.IsPathRooted(scopeRelative)||scopeRelative.Split('\\','/').Any(p=>p is "." or "..")||scopeRelative.Contains(':')))throw new ArgumentException("核对范围必须在根目录内。");
        foreach(var exclusion in exclusions)
            if(Path.IsPathRooted(exclusion.RelativePath)||exclusion.RelativePath.Split('\\','/').Any(p=>p is ".." or ".")||exclusion.RelativePath.Contains(':'))
                throw new ArgumentException("扫描排除必须是根目录内的相对路径。");
        string? executable=scanWorkerExecutable??ScanWorkerClient.FindExecutable();
        if(executable is null && RequiresIsolation(root))throw new NotSupportedException("目录扫描工作进程缺失，无法安全扫描远程或可移动磁盘。");
        await using var worker=executable is null?null:new ScanWorkerClient(executable);
        await using var probe=executable is null?null:new ScanWorkerClient(executable);
        var renames=new ScanRenames(catalog,probe);
        var dirtyStore=new ScanDirtyDirectories(catalog);
        IReadOnlyList<DirtyScanScope> captured=scopeRelative is null?await dirtyStore.Read(rootId,epoch,cancellation,includeDeferred:true).ConfigureAwait(false):[];
        string scanId=Guid.NewGuid().ToString("N");long files=0,dirs=0,errors=0;bool allowCloud=false,incomplete=false,initialized=false;string availability="online",outcome="failed";
        try
        {
        await catalog.Write(c=>
        {
            using var t=c.BeginTransaction();EnsureEpoch(c,t,rootId,epoch);
            ScanRecovery.Recover(c,t);
            ScanRenames.Ensure(c,t);ScanDirtyDirectories.Ensure(c,t);
            Execute(c,t,"CREATE TEMP TABLE IF NOT EXISTS ScanQueue(queue_id INTEGER PRIMARY KEY AUTOINCREMENT,scan_id TEXT NOT NULL,directory_id TEXT NOT NULL,relative_path TEXT NOT NULL,subtree INTEGER NOT NULL,path_depth INTEGER NOT NULL,UNIQUE(scan_id,directory_id))");
            Execute(c,t,"CREATE INDEX IF NOT EXISTS temp.IX_ScanQueue_Order ON ScanQueue(scan_id,path_depth DESC,queue_id)");
            using var policy=c.CreateCommand();policy.Transaction=t;policy.CommandText="SELECT cloud_policy FROM Roots WHERE root_id=$id";policy.Parameters.AddWithValue("$id",rootId);allowCloud=(string?)policy.ExecuteScalar()=="explicitAllowed";
            Execute(c,t,"INSERT INTO ScanRuns(scan_id,root_id,root_epoch,state,started_utc_ticks) VALUES($scan,$root,$epoch,'running',$now)",("$scan",scanId),("$root",rootId),("$epoch",epoch),("$now",DateTime.UtcNow.Ticks));
            ScanRecovery.Own(c,t,scanId);
            if(scopeRelative is null)AddDirectory(c,t,rootId,"",null,scanId);
            else
            {
                using var scope=c.CreateCommand();scope.Transaction=t;scope.CommandText="SELECT directory_id FROM Directories WHERE root_id=$root AND canonical_key=$path";scope.Parameters.AddWithValue("$root",rootId);scope.Parameters.AddWithValue("$path",scopeRelative);
                if(scope.ExecuteScalar() is not string existing)throw new DirectoryNotFoundException("待核对目录已不在索引中。");
                Queue(c,t,scanId,existing,scopeRelative,descendants);
            }
            Execute(c,t,"UPDATE Roots SET scan_state='scanning' WHERE root_id=$root",("$root",rootId));t.Commit();initialized=true;return true;
        },cancellation).ConfigureAwait(false);
            if(scopeRelative is not null)
            {
                var rootState=probe is null?await Task.Run(()=>ScanPathProbe.Read(root),cancellation).ConfigureAwait(false):await probe.Probe(root,cancellation).ConfigureAwait(false);
                if(rootState.State!="present"){availability=rootState.State=="inaccessible"?"inaccessible":"offline";throw new IOException("根目录当前不可用，保留待核对范围。");}
                await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT volume_identity FROM Roots WHERE root_id=$id";cmd.Parameters.AddWithValue("$id",rootId);if(cmd.ExecuteScalar() is string expected&&rootState.PhysicalIdentity is string actual&&expected!=actual)throw new RootIdentityChangedException();return true;},cancellation).ConfigureAwait(false);
            }
            while(true)
            {
                cancellation.ThrowIfCancellationRequested();
                var queued=await catalog.Write(c=>
                {
                    using var t=c.BeginTransaction();EnsureEpoch(c,t,rootId,epoch);
                    using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT q.directory_id,q.relative_path,q.subtree FROM temp.ScanQueue q JOIN DirectoryScans s ON s.scan_id=q.scan_id AND s.directory_id=q.directory_id WHERE q.scan_id=$scan AND s.state='queued' ORDER BY q.path_depth DESC,q.queue_id LIMIT 1";cmd.Parameters.AddWithValue("$scan",scanId);
                    (string Id,string Path,bool Subtree)? next=null;using(var row=cmd.ExecuteReader())if(row.Read())next=(row.GetString(0),row.GetString(1),row.GetInt64(2)!=0);
                    if(next is {} found)Execute(c,t,"DELETE FROM temp.ScanQueue WHERE scan_id=$scan AND directory_id=$id",("$scan",scanId),("$id",found.Id));
                    t.Commit();return next;
                },cancellation).ConfigureAwait(false);
                if(queued is not {} work)break;
                string relative=work.Path,id=work.Id,path=Path.Combine(root,relative);long directoryEntries=0;bool terminal=false;
                await SetDirectoryState(rootId,epoch,scanId,id,"enumerating",null,0,cancellation).ConfigureAwait(false);
                if(exclusions.Any(e=>e.Mode=="skipScan"&&Within(relative,e.RelativePath)))
                {await ExcludeDirectory(rootId,epoch,scanId,id,relative,"ScanExcluded",cancellation).ConfigureAwait(false);continue;}
                try
                {
                    var packets=worker is null?ReadLocal(path,allowCloud,cancellation):worker.Read(path,allowCloud,cancellation);
                    await foreach(var packet in packets.ConfigureAwait(false))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if(packet.State=="started")
                        {
                            await catalog.Write(c=>
                            {
                                using var t=c.BeginTransaction();EnsureEpoch(c,t,rootId,epoch);
                                if(relative.Length==0 && packet.PhysicalIdentity is not null)
                                {
                                    using var check=c.CreateCommand();check.Transaction=t;check.CommandText="SELECT volume_identity FROM Roots WHERE root_id=$r";check.Parameters.AddWithValue("$r",rootId);
                                    string? recorded=check.ExecuteScalar() as string;
                                    if(recorded is not null && recorded!=packet.PhysicalIdentity)throw new RootIdentityChangedException();
                                    Execute(c,t,"UPDATE Roots SET volume_identity=$identity WHERE root_id=$r",("$identity",packet.PhysicalIdentity),("$r",rootId));
                                }
                                Execute(c,t,"UPDATE Directories SET case_mode=$case WHERE directory_id=$id",("$case",packet.CaseMode),("$id",id));t.Commit();return true;
                            },cancellation).ConfigureAwait(false);
                        }
                        if(packet.Entries.Length>0)
                        {
                            long started=System.Diagnostics.Stopwatch.GetTimestamp();
                            var moves=await renames.Find(rootId,root,id,relative,packet.Entries,cancellation).ConfigureAwait(false);
                            RenameLookupTime+=System.Diagnostics.Stopwatch.GetElapsedTime(started);
                            started=System.Diagnostics.Stopwatch.GetTimestamp();
                            await CommitBatch(rootId,epoch,id,relative,scanId,packet.Entries,recursive,exclusions,forceRefresh,work.Subtree,moves,cancellation).ConfigureAwait(false);
                            BatchWriteTime+=System.Diagnostics.Stopwatch.GetElapsedTime(started);
                            files+=packet.Entries.LongCount(e=>!e.Directory&&e.SkipReason is null);directoryEntries+=packet.Entries.Length;
                            incomplete|=packet.Entries.Any(e=>e.Directory&&e.SkipReason=="DeferredOffline");
                            progress?.Report(new(files,dirs,errors,"scanning"));
                        }
                        if(packet.State=="completed")
                        {await Reconcile(rootId,epoch,scanId,id,directoryEntries,cancellation).ConfigureAwait(false);dirs++;terminal=true;}
                        else if(packet.State=="excluded")
                        {incomplete|=packet.ErrorCode=="DeferredOffline";if(relative.Length==0)availability="unknown";await ExcludeDirectory(rootId,epoch,scanId,id,relative,packet.ErrorCode??"Excluded",cancellation).ConfigureAwait(false);terminal=true;}
                        else if(packet.State is "offline" or "inaccessible" or "failed")
                        {
                            errors++;if(relative.Length==0)availability=packet.State=="inaccessible"?"inaccessible":"offline";
                            await SetDirectoryState(rootId,epoch,scanId,id,packet.State,packet.ErrorCode,directoryEntries,cancellation).ConfigureAwait(false);terminal=true;
                            if(scopeRelative is null)await dirtyStore.Mark(rootId,epoch,[new(relative,"EnumerationIncomplete",true)],cancellation,onlyIfMissing:true).ConfigureAwait(false);
                        }
                    }
                    if(!terminal)throw new IOException("扫描进程没有返回目录完成状态。");
                }
                catch(RootIdentityChangedException){availability="unknown";throw;}
                catch(ScanWorkerUnavailableException){throw;}
                catch(Exception ex) when(ex is UnauthorizedAccessException or IOException or TimeoutException)
                {
                    errors++;string state=ex is UnauthorizedAccessException?"inaccessible":ex is TimeoutException?"failed":"offline";
                    if(relative.Length==0)availability=state=="inaccessible"?"inaccessible":"offline";
                    await SetDirectoryState(rootId,epoch,scanId,id,state,ex is TimeoutException?"EnumerationTimeout":state=="inaccessible"?"AccessDenied":"EnumerationIoFailure",directoryEntries,cancellation).ConfigureAwait(false);
                    if(scopeRelative is null)await dirtyStore.Mark(rootId,epoch,[new(relative,"EnumerationIncomplete",true)],cancellation,onlyIfMissing:true).ConfigureAwait(false);
                }
            }
            outcome=errors==0&&!incomplete?"ready":"partial";
            if(outcome=="ready")foreach(var scope in captured)await dirtyStore.Acknowledge(rootId,epoch,scope,cancellation).ConfigureAwait(false);
        }
        catch(OperationCanceledException){outcome="cancelled";}
        finally
        {
            if(initialized)await catalog.Write(c=>
            {
                using var t=c.BeginTransaction();
                Execute(c,t,"UPDATE ScanRuns SET state=$state,completed_utc_ticks=$now,error_count=$errors WHERE scan_id=$scan",("$state",outcome=="ready"?"completed":outcome),("$now",DateTime.UtcNow.Ticks),("$errors",errors),("$scan",scanId));
                Execute(c,t,"UPDATE DirectoryScans SET state=$state,error_code=$error WHERE scan_id=$scan AND state IN ('queued','enumerating')",("$state",outcome=="cancelled"?"cancelled":"failed"),("$error",outcome=="cancelled"?"Cancelled":"ScanTerminated"),("$scan",scanId));
                Execute(c,t,"UPDATE Roots SET scan_state=$state,availability=$availability,last_checked_utc_ticks=$now WHERE root_id=$root AND root_epoch=$epoch",("$state",outcome),("$availability",availability),("$now",DateTime.UtcNow.Ticks),("$root",rootId),("$epoch",epoch));
                Execute(c,t,"DELETE FROM temp.ScanQueue WHERE scan_id=$scan",("$scan",scanId));
                Execute(c,t,"DELETE FROM ScanOwners WHERE scan_id=$scan",("$scan",scanId));
                Execute(c,t,"UPDATE SchemaInfo SET catalog_revision=catalog_revision+1");t.Commit();return true;
            }).ConfigureAwait(false);
        }
        var result=new ScanProgress(files,dirs,errors,outcome);progress?.Report(result);return result;
    }

    public async Task<ScanProgress> ReconcileDirty(string rootId,string root,long epoch,bool recursive,ExclusionSpec[] exclusions,IProgress<ScanProgress>? progress,CancellationToken cancellation)
    {
        var dirty=new ScanDirtyDirectories(catalog);long files=0,directories=0,errors=0;
        while(true)
        {
            var pending=await dirty.Read(rootId,epoch,cancellation).ConfigureAwait(false);if(pending.Count==0)break;
            foreach(var scope in pending)
            {
                cancellation.ThrowIfCancellationRequested();
                string? currentPath=await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT relative_path FROM Directories WHERE directory_id=$id AND root_id=$root AND entry_state<>'missing' AND canonical_key=relative_path";cmd.Parameters.AddWithValue("$id",scope.DirectoryId);cmd.Parameters.AddWithValue("$root",rootId);return cmd.ExecuteScalar() as string;},cancellation).ConfigureAwait(false);
                if(currentPath is null){await dirty.Acknowledge(rootId,epoch,scope,cancellation).ConfigureAwait(false);continue;}
                try
                {
                    var result=await Scan(rootId,root,epoch,recursive,exclusions,progress,cancellation,scopeRelative:currentPath,descendants:scope.Subtree).ConfigureAwait(false);
                    files+=result.Files;directories+=result.Directories;errors+=result.Errors;
                    if(result.State=="cancelled")return new(files,directories,errors,"cancelled");
                    if(result.State=="ready")await dirty.Acknowledge(rootId,epoch,scope,cancellation).ConfigureAwait(false);
                    else await dirty.RetryLater(rootId,epoch,scope,cancellation).ConfigureAwait(false);
                }
                catch(RootIdentityChangedException){throw;}
                catch(ScanWorkerUnavailableException){throw;}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or TimeoutException)
                {errors++;await dirty.RetryLater(rootId,epoch,scope,cancellation).ConfigureAwait(false);}
            }
        }
        bool remaining=(await dirty.Read(rootId,epoch,cancellation,includeDeferred:true).ConfigureAwait(false)).Count>0;
        var final=new ScanProgress(files,directories,errors,remaining?"partial":"ready");progress?.Report(final);return final;
    }

    private Task<bool> CommitBatch(string root,long epoch,string directoryId,string relative,string scan,ScanEntry[] entries,bool recursive,ExclusionSpec[] exclusions,bool forceRefresh,bool descendants,Dictionary<string,ScanRename> moves,CancellationToken cancellation)=>catalog.Write(c=>
    {
        using var t=c.BeginTransaction();EnsureEpoch(c,t,root,epoch);using var commands=new BatchCommands(c,t);
        // Move identities before upserting a new file that may already occupy one of the old source paths.
        foreach(var change in moves)ScanRenames.Apply(c,t,root,directoryId,change.Key,change.Value);
        const string changed="(Files.stat_signature<>excluded.stat_signature OR Files.entry_state<>excluded.entry_state OR $force=1)";
        string[] metadata=["format_id","is_raw","is_animated","display_width","display_height","long_edge","short_edge","pixel_count","encoded_width","encoded_height","orientation","bit_depth","frame_count","page_count","duration_ms","fps_num","fps_den","video_codec","audio_codec","capture_wall_ticks","capture_offset_minutes","capture_utc_ticks","source_metadata_version"];
        string reset=string.Join(",",metadata.Select(field=>$"{field}=CASE WHEN {changed} THEN NULL ELSE Files.{field} END"));
        foreach(var item in entries)
        {
            string path=relative.Length==0?item.Name:Path.Combine(relative,item.Name);
            long initialVersion=ScanRenames.RetireReplacement(c,t,root,path,item);
            string id=ScanRenames.IdForPath(c,t,root,path,item.Directory);
            if(item.Directory)
            {
                using var known=c.CreateCommand();known.Transaction=t;known.CommandText="SELECT 1 FROM Directories WHERE root_id=$root AND canonical_key=$path AND entry_state='present'";known.Parameters.AddWithValue("$root",root);known.Parameters.AddWithValue("$path",path);bool existed=known.ExecuteScalar() is not null;
                id=AddDirectory(c,t,root,path,directoryId,scan,item.CaseMode,descendants||!existed,item.PhysicalIdentity);
                string? reason=item.SkipReason??(!recursive?"NonRecursive":exclusions.Any(e=>e.Mode=="skipScan"&&Within(path,e.RelativePath))?"ScanExcluded":null);
                if(reason is not null)Exclude(c,t,root,scan,id,path,reason);
                continue;
            }
            string extension=Path.GetExtension(item.Name).ToLowerInvariant(),kind=FileKinds.Candidate(item.Name);
            string signature=$"{item.Bytes}:{item.Modified}:{item.Created}:{item.ChangeTime}:{item.PhysicalIdentity}:{item.Attributes}";
            commands.Execute($"""
            INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,allocated_bytes,physical_identity,mtime_utc_ticks,ctime_utc_ticks,file_attributes,hydration_state,entry_state,last_seen_scan_id,updated_revision,file_version)
            VALUES($id,$root,$dir,$name,$ext,$path,$path,$pathkey,$namekey,1,$signature,$kind,'extension',$bytes,$allocated,$physical,$modified,$created,$attributes,$hydration,$state,$scan,0,$initialVersion)
            ON CONFLICT(entry_id) DO UPDATE SET logical_bytes=excluded.logical_bytes,allocated_bytes=excluded.allocated_bytes,physical_identity=excluded.physical_identity,mtime_utc_ticks=excluded.mtime_utc_ticks,ctime_utc_ticks=excluded.ctime_utc_ticks,file_attributes=excluded.file_attributes,hydration_state=excluded.hydration_state,last_seen_scan_id=excluded.last_seen_scan_id,entry_state=excluded.entry_state,
            file_version=Files.file_version+CASE WHEN {changed} THEN 1 ELSE 0 END,
            kind=CASE WHEN {changed} THEN excluded.kind ELSE Files.kind END,kind_confidence=CASE WHEN {changed} THEN 'extension' ELSE Files.kind_confidence END,
            {reset},stat_signature=excluded.stat_signature;
            """,("$id",id),("$root",root),("$dir",directoryId),("$name",item.Name),("$ext",extension),("$path",path),("$pathkey",NaturalOrder.Key(path)),("$namekey",NaturalOrder.Key(item.Name)),("$signature",signature),("$kind",kind),("$bytes",item.Bytes),("$allocated",item.Allocated),("$physical",item.PhysicalIdentity),("$modified",item.Modified),("$created",item.Created),("$attributes",item.Attributes),("$hydration",item.Hydration),("$state",item.SkipReason is null?"present":"excluded"),("$scan",scan),("$force",forceRefresh?1:0),("$initialVersion",initialVersion));
            commands.Execute("DELETE FROM FieldStates WHERE entry_id=$id AND source_version<>(SELECT file_version FROM Files WHERE entry_id=$id)",("$id",id));
            if(item.Hydration=="placeholder")
                foreach(string group in new[]{"identity","imageGeometry","animation","imageColor","captureTime","media","allocation"})
                    commands.Execute("INSERT INTO FieldStates(entry_id,field_group,source_version,state,error_code) SELECT entry_id,$group,file_version,'deferredOffline','LocalOnly' FROM Files WHERE entry_id=$id ON CONFLICT(entry_id,field_group) DO UPDATE SET source_version=excluded.source_version,state='deferredOffline',error_code='LocalOnly'",("$id",id),("$group",group));
        }
        t.Commit();return true;
    },cancellation);
    // Keep the recursive working set first so every descendant lookup binds both
    // columns of IX_Directories_Parent, even in a large multi-root catalog.
    internal const string RemovedDirectoriesSql="WITH RECURSIVE removed(id) AS (SELECT directory_id FROM Directories WHERE root_id=$root AND parent_id=$dir AND entry_state='present' AND (last_seen_scan_id IS NULL OR last_seen_scan_id<>$scan) UNION ALL SELECT d.directory_id FROM removed r CROSS JOIN Directories d ON d.parent_id=r.id WHERE d.root_id=$root) ";
    internal const string ReconcileRemovedFilesSql=RemovedDirectoriesSql+"UPDATE Files SET entry_state='missing',file_version=file_version+1 WHERE root_id=$root AND entry_state='present' AND directory_id IN (SELECT id FROM removed)";
    private Task<bool> Reconcile(string root,long epoch,string scan,string directory,long count,CancellationToken cancellation)=>catalog.Write(c=>
    {
        using var t=c.BeginTransaction();EnsureEpoch(c,t,root,epoch);
        Execute(c,t,"UPDATE Files SET entry_state='missing',file_version=file_version+1 WHERE root_id=$root AND directory_id=$dir AND entry_state<>'missing' AND (last_seen_scan_id IS NULL OR last_seen_scan_id<>$scan)",("$root",root),("$dir",directory),("$scan",scan));
        Execute(c,t,ReconcileRemovedFilesSql,("$root",root),("$dir",directory),("$scan",scan));
        Execute(c,t,RemovedDirectoriesSql+"UPDATE Directories SET entry_state='missing' WHERE directory_id IN (SELECT id FROM removed)",("$root",root),("$dir",directory),("$scan",scan));
        Execute(c,t,"UPDATE DirectoryScans SET state='completed',entry_count=$count,error_code=NULL WHERE scan_id=$scan AND directory_id=$dir",("$count",count),("$scan",scan),("$dir",directory));t.Commit();return true;
    },cancellation);
    private Task<bool> SetDirectoryState(string root,long epoch,string scan,string directory,string state,string? error,long count,CancellationToken cancellation)=>catalog.Write(c=>
    {using var t=c.BeginTransaction();EnsureEpoch(c,t,root,epoch);Execute(c,t,"UPDATE DirectoryScans SET state=$state,error_code=$error,entry_count=$count WHERE scan_id=$scan AND directory_id=$dir",("$state",state),("$error",error),("$count",count),("$scan",scan),("$dir",directory));t.Commit();return true;},cancellation);
    private Task<bool> ExcludeDirectory(string root,long epoch,string scan,string directory,string path,string reason,CancellationToken cancellation)=>catalog.Write(c=>
    {using var t=c.BeginTransaction();EnsureEpoch(c,t,root,epoch);Exclude(c,t,root,scan,directory,path,reason);t.Commit();return true;},cancellation);
    private static void Exclude(SqliteConnection c,SqliteTransaction t,string root,string scan,string directory,string path,string reason)
    {
        Execute(c,t,"UPDATE Directories SET entry_state='excluded' WHERE root_id=$root AND ($path='' OR relative_path=$path OR substr(relative_path,1,$len)=$prefix)",("$root",root),("$path",path),("$len",path.Length+1),("$prefix",path+"\\"));
        Execute(c,t,"UPDATE DirectoryScans SET state='excluded',error_code=$reason WHERE scan_id=$scan AND directory_id IN (SELECT directory_id FROM Directories WHERE root_id=$root AND ($path='' OR relative_path=$path OR substr(relative_path,1,$len)=$prefix))",("$scan",scan),("$root",root),("$reason",reason),("$path",path),("$len",path.Length+1),("$prefix",path+"\\"));
        Execute(c,t,"DELETE FROM temp.ScanQueue WHERE scan_id=$scan AND ($path='' OR relative_path=$path OR substr(relative_path,1,$len)=$prefix)",("$scan",scan),("$path",path),("$len",path.Length+1),("$prefix",path+"\\"));
        Execute(c,t,"UPDATE Files SET entry_state='excluded' WHERE root_id=$root AND ($path='' OR relative_path=$path OR substr(relative_path,1,$len)=$prefix)",("$root",root),("$path",path),("$len",path.Length+1),("$prefix",path+"\\"));
    }
    private static bool Within(string path,string excluded)=>excluded.Length==0||PathRules.IsWithinRelative(path,excluded.Replace('/','\\').TrimEnd('\\'));
    private static string AddDirectory(SqliteConnection c,SqliteTransaction t,string root,string path,string? parent,string scan,string caseMode="unknown",bool enqueue=true,string? physicalIdentity=null)
    {
        string id=ScanRenames.IdForPath(c,t,root,path,true);
        Execute(c,t,"INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode,last_seen_scan_id) VALUES($id,$root,$parent,$name,$path,$path,$case,$scan) ON CONFLICT(directory_id) DO UPDATE SET last_seen_scan_id=excluded.last_seen_scan_id,entry_state='present',case_mode=excluded.case_mode",("$id",id),("$root",root),("$parent",parent),("$name",Path.GetFileName(path)),("$path",path),("$case",caseMode),("$scan",scan));
        if(physicalIdentity is not null)Execute(c,t,"INSERT INTO ScanDirectoryIdentities VALUES($id,$physical) ON CONFLICT(directory_id) DO UPDATE SET physical_identity=excluded.physical_identity",("$id",id),("$physical",physicalIdentity));
        if(enqueue)Queue(c,t,scan,id,path,true);return id;
    }
    private static void Queue(SqliteConnection c,SqliteTransaction t,string scan,string id,string path,bool subtree)
    {
        Execute(c,t,"INSERT OR IGNORE INTO DirectoryScans(scan_id,directory_id,state) VALUES($scan,$id,'queued')",("$scan",scan),("$id",id));
        Execute(c,t,"INSERT OR IGNORE INTO temp.ScanQueue(scan_id,directory_id,relative_path,subtree,path_depth) VALUES($scan,$id,$path,$subtree,$depth)",("$scan",scan),("$id",id),("$path",path),("$subtree",subtree?1:0),("$depth",path.Length==0?0:path.Count(character=>character is '\\' or '/')+1));
    }
    private static void EnsureEpoch(SqliteConnection c,SqliteTransaction t,string root,long epoch)
    {using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT root_epoch FROM Roots WHERE root_id=$root";cmd.Parameters.AddWithValue("$root",root);if((long?)cmd.ExecuteScalar()!=epoch)throw new OperationCanceledException("Root epoch changed.");}
    // The batch owns at most the upsert, stale-field cleanup and offline-field commands.
    // Reuse their prepared statements while preserving the existing transaction boundary.
    private sealed class BatchCommands(SqliteConnection connection,SqliteTransaction transaction):IDisposable
    {
        private readonly Dictionary<string,SqliteCommand> commands=[];
        public int Execute(string sql,params (string Key,object? Value)[] values)
        {
            if(!commands.TryGetValue(sql,out var command))
            {
                command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;
                commands.Add(sql,command);
                foreach(var pair in values)command.Parameters.AddWithValue(pair.Key,pair.Value??DBNull.Value);
                command.Prepare();
            }
            else foreach(var pair in values)command.Parameters[pair.Key].Value=pair.Value??DBNull.Value;
            return command.ExecuteNonQuery();
        }
        public void Dispose(){foreach(var command in commands.Values)command.Dispose();}
    }
    internal static int Execute(SqliteConnection c,SqliteTransaction? transaction,string sql,params (string Key,object? Value)[] values)
    {using var cmd=c.CreateCommand();cmd.Transaction=transaction;cmd.CommandText=sql;foreach(var pair in values)cmd.Parameters.AddWithValue(pair.Key,pair.Value??DBNull.Value);return cmd.ExecuteNonQuery();}
    private static bool RequiresIsolation(string root)=>root.StartsWith(@"\\",StringComparison.Ordinal)||new DriveInfo(Path.GetPathRoot(root)!).DriveType is not (DriveType.Fixed or DriveType.Ram);
    private static async IAsyncEnumerable<ScanDirectoryPacket> ReadLocal(string path,bool allowCloud,[EnumeratorCancellation]CancellationToken cancellation)
    {
        using var iterator=ScanDirectoryReader.Read(path,allowCloud).GetEnumerator();
        while(await Task.Run(iterator.MoveNext,cancellation).ConfigureAwait(false)){cancellation.ThrowIfCancellationRequested();yield return iterator.Current;}
    }
}
