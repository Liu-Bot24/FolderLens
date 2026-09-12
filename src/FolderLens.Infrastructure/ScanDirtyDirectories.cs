using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed record DirtyScanScope(string DirectoryId,string RelativePath,long Revision,bool Subtree);
public sealed record DirectoryChangeHint(string RelativePath,string Reason,bool Subtree=false);

/// <summary>Persistent coalescing hints; revision-checked acknowledgement cannot erase a concurrent watcher event.</summary>
public sealed class ScanDirtyDirectories(CatalogStore catalog)
{
    internal static void Ensure(SqliteConnection c,SqliteTransaction t)
    {
        DirectoryIndexer.Execute(c,t,"CREATE TABLE IF NOT EXISTS ScanDirtyVersions(directory_id TEXT PRIMARY KEY REFERENCES Directories(directory_id) ON DELETE CASCADE,revision INTEGER NOT NULL,subtree INTEGER NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,retry_after_ticks INTEGER NOT NULL DEFAULT 0)");
        DirectoryIndexer.Execute(c,t,"CREATE TABLE IF NOT EXISTS ScanDirtyEpochs(root_id TEXT PRIMARY KEY REFERENCES Roots(root_id) ON DELETE CASCADE,epoch INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS IX_ScanDirtyDue ON ScanDirtyVersions(retry_after_ticks,directory_id); CREATE INDEX IF NOT EXISTS IX_DirtyEarliest ON DirtyDirectories(earliest_utc_ticks,directory_id);");
    }
    public Task<int> Mark(string rootId,long epoch,IReadOnlyList<DirectoryChangeHint> hints,CancellationToken cancellation=default,bool onlyIfMissing=false)=>catalog.Write(c=>
    {
        using var t=c.BeginTransaction();Ensure(c,t);CheckEpoch(c,t,rootId,epoch);int count=0;
        foreach(var hint in hints)
        {
            string path=hint.RelativePath.Replace('/','\\').TrimEnd('\\');
            if(Path.IsPathRooted(path)||path.Split('\\').Any(p=>p is "." or "..")||path.Contains(':'))throw new ArgumentException("无效的核对目录范围。");
            using var nearest=c.CreateCommand();nearest.Transaction=t;nearest.CommandText="SELECT directory_id FROM Directories WHERE root_id=$r AND canonical_key=relative_path AND entry_state<>'missing' AND (relative_path=$p OR relative_path='' OR substr($p,1,length(relative_path)+1)=relative_path||char(92)) ORDER BY length(relative_path) DESC LIMIT 1";nearest.Parameters.AddWithValue("$r",rootId);nearest.Parameters.AddWithValue("$p",path);
            if(nearest.ExecuteScalar() is not string id)continue;
            if(onlyIfMissing)
            {using var existing=c.CreateCommand();existing.Transaction=t;existing.CommandText="SELECT 1 FROM DirtyDirectories WHERE directory_id=$id";existing.Parameters.AddWithValue("$id",id);if(existing.ExecuteScalar() is not null){if(hint.Subtree)DirectoryIndexer.Execute(c,t,"UPDATE ScanDirtyVersions SET subtree=1,revision=revision+1 WHERE directory_id=$id AND subtree=0",("$id",id));count++;continue;}}
            DirectoryIndexer.Execute(c,t,"INSERT INTO DirtyDirectories(directory_id,reason,earliest_utc_ticks,root_epoch) VALUES($id,$reason,$now,$epoch) ON CONFLICT(directory_id) DO UPDATE SET reason=excluded.reason,root_epoch=excluded.root_epoch",("$id",id),("$reason",hint.Reason),("$now",DateTime.UtcNow.Ticks),("$epoch",epoch));
            DirectoryIndexer.Execute(c,t,"INSERT INTO ScanDirtyVersions(directory_id,revision,subtree) VALUES($id,1,$tree) ON CONFLICT(directory_id) DO UPDATE SET revision=revision+1,subtree=max(subtree,excluded.subtree),attempts=0,retry_after_ticks=0",("$id",id),("$tree",hint.Subtree?1:0));count++;
        }
        t.Commit();return count;
    },cancellation);
    public Task<IReadOnlyList<DirtyScanScope>> Read(string rootId,long epoch,CancellationToken cancellation=default,bool includeDeferred=false)=>catalog.Write<IReadOnlyList<DirtyScanScope>>(c=>
    {
        using var t=c.BeginTransaction();Ensure(c,t);CheckEpoch(c,t,rootId,epoch);
        using var adopted=c.CreateCommand();adopted.Transaction=t;adopted.CommandText="SELECT epoch FROM ScanDirtyEpochs WHERE root_id=$root";adopted.Parameters.AddWithValue("$root",rootId);
        if((long?)adopted.ExecuteScalar()!=epoch)
        {
            DirectoryIndexer.Execute(c,t,"UPDATE DirtyDirectories SET root_epoch=$epoch WHERE root_epoch<>$epoch AND directory_id IN (SELECT directory_id FROM Directories WHERE root_id=$root)",("$epoch",epoch),("$root",rootId));
            // Migrate legacy pending ranges once per opened epoch, never rewrite every row on every watcher tick.
            DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO ScanDirtyVersions(directory_id,revision,subtree) SELECT q.directory_id,1,1 FROM DirtyDirectories q JOIN Directories d ON d.directory_id=q.directory_id WHERE d.root_id=$root",("$root",rootId));
            DirectoryIndexer.Execute(c,t,"INSERT INTO ScanDirtyEpochs VALUES($root,$epoch) ON CONFLICT(root_id) DO UPDATE SET epoch=excluded.epoch",("$root",rootId),("$epoch",epoch));
        }
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT d.directory_id,d.relative_path,v.revision,v.subtree FROM DirtyDirectories q JOIN Directories d ON d.directory_id=q.directory_id JOIN ScanDirtyVersions v ON v.directory_id=d.directory_id WHERE d.root_id=$r AND ($all=1 OR v.retry_after_ticks<=$now) ORDER BY q.earliest_utc_ticks,d.directory_id LIMIT 128";cmd.Parameters.AddWithValue("$r",rootId);cmd.Parameters.AddWithValue("$all",includeDeferred?1:0);cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
        var scopes=new List<DirtyScanScope>();using(var rows=cmd.ExecuteReader())while(rows.Read())scopes.Add(new(rows.GetString(0),rows.GetString(1),rows.GetInt64(2),rows.GetInt64(3)!=0));t.Commit();return scopes;
    },cancellation);
    public Task Acknowledge(string rootId,long epoch,DirtyScanScope scope,CancellationToken cancellation=default)=>catalog.Write(c=>
    {
        using var t=c.BeginTransaction();CheckEpoch(c,t,rootId,epoch);
        DirectoryIndexer.Execute(c,t,"DELETE FROM DirtyDirectories WHERE directory_id=$id AND root_epoch=$epoch AND EXISTS(SELECT 1 FROM ScanDirtyVersions v WHERE v.directory_id=$id AND v.revision=$revision)",("$id",scope.DirectoryId),("$epoch",epoch),("$revision",scope.Revision));
        DirectoryIndexer.Execute(c,t,"DELETE FROM ScanDirtyVersions WHERE directory_id=$id AND NOT EXISTS(SELECT 1 FROM DirtyDirectories WHERE directory_id=$id)",("$id",scope.DirectoryId));t.Commit();return true;
    },cancellation);
    public Task RetryLater(string rootId,long epoch,DirtyScanScope scope,CancellationToken cancellation=default)=>catalog.Write(c=>
    {
        using var t=c.BeginTransaction();CheckEpoch(c,t,rootId,epoch);
        DirectoryIndexer.Execute(c,t,"UPDATE ScanDirtyVersions SET retry_after_ticks=$now+(CASE attempts WHEN 0 THEN 5 WHEN 1 THEN 15 WHEN 2 THEN 60 ELSE 300 END)*10000000,attempts=min(3,attempts+1) WHERE directory_id=$id AND revision=$revision",("$now",DateTime.UtcNow.Ticks),("$id",scope.DirectoryId),("$revision",scope.Revision));t.Commit();return true;
    },cancellation);
    private static void CheckEpoch(SqliteConnection c,SqliteTransaction t,string root,long epoch)
    {using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT root_epoch FROM Roots WHERE root_id=$r";cmd.Parameters.AddWithValue("$r",root);if((long?)cmd.ExecuteScalar()!=epoch)throw new OperationCanceledException("Root epoch changed.");}
}
