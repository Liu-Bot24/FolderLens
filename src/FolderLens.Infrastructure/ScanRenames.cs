using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

internal sealed record ScanRename(string EntryId,string OldPath,string PhysicalIdentity,bool Directory);
internal sealed class ScanRenames(CatalogStore catalog,ScanWorkerClient? probe,Func<string,CancellationToken,Task<ScanDirectoryPacket>>? probeOverride=null)
{
    public static void Ensure(SqliteConnection c,SqliteTransaction t)
    {
        DirectoryIndexer.Execute(c,t,"CREATE TABLE IF NOT EXISTS ScanDirectoryIdentities(directory_id TEXT PRIMARY KEY REFERENCES Directories(directory_id) ON DELETE CASCADE,physical_identity TEXT NOT NULL)");
        DirectoryIndexer.Execute(c,t,"CREATE INDEX IF NOT EXISTS IX_ScanDirectoryIdentity ON ScanDirectoryIdentities(physical_identity)");
        DirectoryIndexer.Execute(c,t,"CREATE TABLE IF NOT EXISTS ScanRetiredStates(kind TEXT NOT NULL,entry_id TEXT NOT NULL,original_state TEXT NOT NULL,PRIMARY KEY(kind,entry_id))");
        DirectoryIndexer.Execute(c,t,"CREATE TABLE IF NOT EXISTS ScanPathVersions(root_id TEXT NOT NULL,relative_path TEXT NOT NULL,last_version INTEGER NOT NULL,PRIMARY KEY(root_id,relative_path))");
        c.CreateFunction("scan_natural",(string path)=>NaturalOrder.Key(path),true);
    }
    public async Task<Dictionary<string,ScanRename>> Find(string rootId,string root,string parentId,string relative,ScanEntry[] entries,CancellationToken cancellation)
    {
        var result=new Dictionary<string,ScanRename>(StringComparer.Ordinal);
        foreach(var item in entries)
        {
            if(item.PhysicalIdentity is not {} identity||item.SkipReason is not null)continue;
            string path=relative.Length==0?item.Name:Path.Combine(relative,item.Name);
            // These bounded identity lookups are part of the scanner's write pipeline.
            // The general reader builds entire snapshots; queuing here would stop file
            // discovery behind every background view rebuild. No filesystem I/O runs here.
            var candidates=await catalog.Write(c=>
            {
                using var current=c.CreateCommand();current.CommandText=item.Directory?"SELECT d.directory_id FROM Directories d JOIN ScanDirectoryIdentities i ON i.directory_id=d.directory_id WHERE d.root_id=$r AND d.canonical_key=$p AND i.physical_identity=$identity":"SELECT entry_id FROM Files WHERE root_id=$r AND canonical_key=$p AND physical_identity=$identity";
                current.Parameters.AddWithValue("$r",rootId);current.Parameters.AddWithValue("$p",path);current.Parameters.AddWithValue("$identity",identity);
                if(current.ExecuteScalar() is not null)return new List<ScanRename>();
                using var find=c.CreateCommand();find.CommandText=item.Directory?
                    "SELECT d.directory_id,d.relative_path FROM Directories d JOIN ScanDirectoryIdentities i ON i.directory_id=d.directory_id WHERE d.root_id=$r AND i.physical_identity=$identity LIMIT 129":
                    "SELECT entry_id,relative_path FROM Files WHERE root_id=$r AND physical_identity=$identity LIMIT 129";
                find.Parameters.AddWithValue("$r",rootId);find.Parameters.AddWithValue("$identity",identity);
                using var rows=find.ExecuteReader();var matches=new List<ScanRename>();while(rows.Read())matches.Add(new(rows.GetString(0),rows.GetString(1),identity,item.Directory));return matches;
            },cancellation).ConfigureAwait(false);
            if(candidates.Count is 0 or >128)continue;
            string parentMode=await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT case_mode FROM Directories WHERE directory_id=$id";cmd.Parameters.AddWithValue("$id",parentId);return cmd.ExecuteScalar() as string??"unknown";},cancellation).ConfigureAwait(false);
            ScanRename? match=null;bool ambiguous=false;
            foreach(var old in candidates)
            {
                if(old.OldPath==path)continue;
                bool caseRename=parentMode=="insensitive"&&string.Equals(old.OldPath,path,StringComparison.OrdinalIgnoreCase)&&Path.GetDirectoryName(old.OldPath)==Path.GetDirectoryName(path);
                var state=await Probe(Path.Combine(root,old.OldPath),cancellation).ConfigureAwait(false);
                bool disappeared=state.State=="missing"||(state.State=="present"&&state.PhysicalIdentity is not null&&state.PhysicalIdentity!=identity);
                if(!disappeared&&!caseRename)continue;
                if(match is not null){ambiguous=true;break;}match=old;
            }
            // Several vanished hard-link paths have no intrinsic rename ordering. Preserve separate entries rather than guessing.
            if(!ambiguous&&match is not null&&(await Probe(Path.Combine(root,path),cancellation).ConfigureAwait(false)).PhysicalIdentity==identity)result[path]=match;
        }
        return result;
    }
    private Task<ScanDirectoryPacket> Probe(string path,CancellationToken cancellation)=>probeOverride is not null?probeOverride(path,cancellation):probe is not null?probe.Probe(path,cancellation):Task.Run(()=>ScanPathProbe.Read(path),cancellation);
    private async Task<bool> MissingWithinKnownNamespace(string path,string identity,CancellationToken cancellation)
    {
        string? parent=Path.GetDirectoryName(path.TrimEnd('\\'));
        for(int depth=0;parent is not null&&depth<64;depth++,parent=Path.GetDirectoryName(parent.TrimEnd('\\')))
        {
            var state=await Probe(parent,cancellation).ConfigureAwait(false);
            if(state.State=="present")return state.VolumeIdentity is {} volume&&identity.StartsWith(volume+":",StringComparison.Ordinal);
            if(state.State!="missing")return false;
        }
        return false;
    }
    public async Task RetireConfirmedMovedRoot(string rootId,string path,long epoch,string identity,CancellationToken cancellation)
    {
        var candidates=await catalog.Write(c=>
        {
            using var command=c.CreateCommand();command.CommandText="""
                SELECT d.directory_id,d.root_id,r.display_path,d.relative_path,r.root_epoch,d.path_revision,b.location_id,b.binding_revision,l.anchor_locator
                FROM ScanDirectoryIdentities i JOIN Directories d ON d.directory_id=i.directory_id JOIN Roots r ON r.root_id=d.root_id
                JOIN DirectoryLocationBindings b ON b.directory_id=d.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id
                WHERE i.physical_identity=$identity AND d.root_id<>$root AND d.entry_state<>'missing'
                UNION
                SELECT d.directory_id,d.root_id,r.display_path,d.relative_path,r.root_epoch,d.path_revision,b.location_id,b.binding_revision,l.anchor_locator
                FROM Roots r JOIN Directories d ON d.root_id=r.root_id AND d.relative_path=''
                JOIN DirectoryLocationBindings b ON b.directory_id=d.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id
                WHERE r.volume_identity=$identity AND r.root_id<>$root AND d.entry_state<>'missing'
                LIMIT 129
                """;
            command.Parameters.AddWithValue("$identity",identity);command.Parameters.AddWithValue("$root",rootId);
            using var rows=command.ExecuteReader();var found=new List<(string Id,string Root,string RootPath,string Relative,long Epoch,long PathRevision,string Location,long Binding,string? Locator)>();
            while(rows.Read())found.Add((rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetInt64(4),rows.GetInt64(5),rows.GetString(6),rows.GetInt64(7),rows.IsDBNull(8)?null:rows.GetString(8)));
            return found;
        },cancellation).ConfigureAwait(false);
        if(candidates.Count>128)return;
        foreach(var old in candidates)
        {
            string oldPath=Path.Combine(old.RootPath,old.Relative);
            if(string.Equals(oldPath,path,StringComparison.Ordinal))continue;
            var previous=await Probe(oldPath,cancellation).ConfigureAwait(false);
            bool renamedSpelling=previous.State=="present"&&previous.PhysicalIdentity==identity&&old.Locator is not null&&previous.ResolvedLocation is not null&&previous.ResolvedLocation!=old.Locator;
            if(!renamedSpelling&&previous.State!="missing"&&!(previous.State=="present"&&previous.PhysicalIdentity is {} actual&&actual!=identity))continue;
            // A vanished drive/share route is not proof that its directory entry
            // was deleted. Require the old immediate parent namespace to exist.
            if(previous.State=="missing")
            {
                if(!await MissingWithinKnownNamespace(oldPath,identity,cancellation).ConfigureAwait(false))continue;
            }
            if((await Probe(path,cancellation).ConfigureAwait(false)).PhysicalIdentity!=identity)throw new IOException("根目录在身份核对期间发生变化。");
            await catalog.Write(c=>
            {
                using var transaction=c.BeginTransaction();using var check=c.CreateCommand();check.Transaction=transaction;
                check.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM Roots WHERE root_id=$root AND root_epoch=$epoch AND display_path=$path)
                       AND EXISTS(SELECT 1 FROM Directories d JOIN Roots r ON r.root_id=d.root_id
                           LEFT JOIN ScanDirectoryIdentities i ON i.directory_id=d.directory_id
                           JOIN DirectoryLocationBindings b ON b.directory_id=d.directory_id
                           WHERE d.directory_id=$id AND d.root_id=$oldRoot AND r.root_epoch=$oldEpoch
                           AND r.display_path=$oldPath AND d.relative_path=$relative AND d.entry_state<>'missing'
                           AND d.path_revision=$pathRevision AND b.location_id=$location AND b.binding_revision=$binding
                           AND coalesce(i.physical_identity,CASE WHEN d.relative_path='' THEN r.volume_identity END)=$identity)
                    """;
                foreach(var (name,value) in new (string,object)[]{("$root",rootId),("$epoch",epoch),("$path",path),("$id",old.Id),("$oldRoot",old.Root),("$oldEpoch",old.Epoch),("$oldPath",old.RootPath),("$relative",old.Relative),("$identity",identity),("$pathRevision",old.PathRevision),("$location",old.Location),("$binding",old.Binding)})check.Parameters.AddWithValue(name,value);
                if((long)check.ExecuteScalar()! !=1)return false;
                RetireTree(c,transaction,old.Root,old.Id);transaction.Commit();return true;
            },cancellation).ConfigureAwait(false);
        }
    }
    public static long RetireReplacement(SqliteConnection c,SqliteTransaction t,string root,string path,ScanEntry item)
    {
        if(item.PhysicalIdentity is null)return 1;
        using var find=c.CreateCommand();find.Transaction=t;find.CommandText=item.Directory?
            "SELECT d.directory_id,i.physical_identity,1 FROM Directories d JOIN ScanDirectoryIdentities i ON i.directory_id=d.directory_id WHERE d.root_id=$root AND d.canonical_key=$path":
            "SELECT entry_id,physical_identity,file_version FROM Files WHERE root_id=$root AND canonical_key=$path";
        find.Parameters.AddWithValue("$root",root);find.Parameters.AddWithValue("$path",path);
        string? id=null,physical=null;long version=1;using(var row=find.ExecuteReader())if(row.Read()){id=row.GetString(0);physical=row.IsDBNull(1)?null:row.GetString(1);version=row.GetInt64(2);}
        if(id is null&&!item.Directory)
        {using var previous=c.CreateCommand();previous.Transaction=t;previous.CommandText="SELECT last_version FROM ScanPathVersions WHERE root_id=$root AND relative_path=$path";previous.Parameters.AddWithValue("$root",root);previous.Parameters.AddWithValue("$path",path);return previous.ExecuteScalar() is long last?checked(last+1):1;}
        if(id is null||physical is null||physical==item.PhysicalIdentity)return version;
        if(item.Directory)RetireTree(c,t,root,id);else RetireFile(c,t,id);
        return checked(version+1);
    }
    private static void RetireFile(SqliteConnection c,SqliteTransaction t,string id)
    {
        RememberFileVersion(c,t,id);
        DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO ScanRetiredStates SELECT 'file',entry_id,entry_state FROM Files WHERE entry_id=$id",("$id",id));
        DirectoryIndexer.Execute(c,t,"UPDATE Files SET canonical_key=relative_path||char(0)||'retired:'||entry_id,entry_state='missing',file_version=file_version+1 WHERE entry_id=$id",("$id",id));
    }
    private static void TreeIds(SqliteConnection c,SqliteTransaction t,string root,string id)
    {
        DirectoryIndexer.Execute(c,t,"CREATE TEMP TABLE IF NOT EXISTS ScanTreeIds(id TEXT PRIMARY KEY); DELETE FROM temp.ScanTreeIds;");
        DirectoryIndexer.Execute(c,t,"WITH RECURSIVE tree(id) AS (SELECT $id UNION ALL SELECT d.directory_id FROM tree CROSS JOIN Directories d WHERE d.root_id=$root AND d.parent_id=tree.id) INSERT INTO temp.ScanTreeIds SELECT id FROM tree",("$id",id),("$root",root));
    }
    private static void RetireTree(SqliteConnection c,SqliteTransaction t,string root,string id)
    {
        TreeIds(c,t,root,id);
        RememberTreeVersions(c,t,root);
        DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO ScanRetiredStates SELECT 'file',entry_id,entry_state FROM Files WHERE root_id=$root AND directory_id IN (SELECT id FROM temp.ScanTreeIds); INSERT OR IGNORE INTO ScanRetiredStates SELECT 'directory',directory_id,entry_state FROM Directories WHERE directory_id IN (SELECT id FROM temp.ScanTreeIds);",("$root",root));
        DirectoryIndexer.Execute(c,t,"UPDATE Files SET canonical_key=relative_path||char(0)||'retired:'||entry_id,entry_state='missing',file_version=file_version+1 WHERE root_id=$root AND directory_id IN (SELECT id FROM temp.ScanTreeIds); UPDATE Directories SET canonical_key=relative_path||char(0)||'retired:'||directory_id,entry_state='missing' WHERE directory_id IN (SELECT id FROM temp.ScanTreeIds);",("$root",root));
    }
    private static void RememberFileVersion(SqliteConnection c,SqliteTransaction t,string id)=>DirectoryIndexer.Execute(c,t,"INSERT INTO ScanPathVersions SELECT root_id,relative_path,file_version FROM Files WHERE entry_id=$id ON CONFLICT(root_id,relative_path) DO UPDATE SET last_version=max(last_version,excluded.last_version)",("$id",id));
    private static void RememberTreeVersions(SqliteConnection c,SqliteTransaction t,string root)=>DirectoryIndexer.Execute(c,t,"INSERT INTO ScanPathVersions SELECT root_id,relative_path,file_version FROM Files WHERE root_id=$root AND directory_id IN (SELECT id FROM temp.ScanTreeIds) ON CONFLICT(root_id,relative_path) DO UPDATE SET last_version=max(last_version,excluded.last_version)",("$root",root));
    public static string IdForPath(SqliteConnection c,SqliteTransaction t,string root,string path,bool directory)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=directory?"SELECT directory_id FROM Directories WHERE root_id=$r AND canonical_key=$p":"SELECT entry_id FROM Files WHERE root_id=$r AND canonical_key=$p";cmd.Parameters.AddWithValue("$r",root);cmd.Parameters.AddWithValue("$p",path);
        if(cmd.ExecuteScalar() is string known)return known;
        string proposed=DirectoryIndexer.StablePathId(root,path);cmd.CommandText=directory?"SELECT 1 FROM Directories WHERE directory_id=$id":"SELECT 1 FROM Files WHERE entry_id=$id";cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$id",proposed);
        return cmd.ExecuteScalar() is null?proposed:Guid.NewGuid().ToString("N");
    }
    public static void Apply(SqliteConnection c,SqliteTransaction t,string root,string parent,string path,ScanRename move)
    {
        using var check=c.CreateCommand();check.Transaction=t;check.CommandText=move.Directory?"SELECT 1 FROM Directories d JOIN ScanDirectoryIdentities i ON i.directory_id=d.directory_id WHERE d.directory_id=$id AND d.root_id=$root AND d.relative_path=$old AND i.physical_identity=$physical":"SELECT 1 FROM Files WHERE entry_id=$id AND root_id=$root AND relative_path=$old AND physical_identity=$physical";
        check.Parameters.AddWithValue("$id",move.EntryId);check.Parameters.AddWithValue("$root",root);check.Parameters.AddWithValue("$old",move.OldPath);check.Parameters.AddWithValue("$physical",move.PhysicalIdentity);if(check.ExecuteScalar() is null)return;
        if(!move.Directory)
        {
            RememberFileVersion(c,t,move.EntryId);
            using var target=c.CreateCommand();target.Transaction=t;target.CommandText="SELECT entry_id FROM Files WHERE root_id=$root AND canonical_key=$path AND entry_id<>$id";target.Parameters.AddWithValue("$root",root);target.Parameters.AddWithValue("$path",path);target.Parameters.AddWithValue("$id",move.EntryId);if(target.ExecuteScalar() is string occupied)RetireFile(c,t,occupied);
            DirectoryIndexer.Execute(c,t,"UPDATE Files SET directory_id=$parent,relative_path=$path,canonical_key=$path,name=$name,extension=$ext,name_sort_key=scan_natural($name),path_sort_key=scan_natural($path),path_revision=path_revision+1 WHERE entry_id=$id",("$parent",parent),("$path",path),("$name",Path.GetFileName(path)),("$ext",Path.GetExtension(path).ToLowerInvariant()),("$id",move.EntryId));DirectoryIndexer.Execute(c,t,"DELETE FROM ScanRetiredStates WHERE kind='file' AND entry_id=$id",("$id",move.EntryId));return;
        }
        // A directory rename retains every descendant identity and snapshot reference; only paths and sort keys advance.
        string old=move.OldPath;string prefix=old+"\\";
        using(var target=c.CreateCommand()){target.Transaction=t;target.CommandText="SELECT directory_id FROM Directories WHERE root_id=$root AND canonical_key=$path AND directory_id<>$id";target.Parameters.AddWithValue("$root",root);target.Parameters.AddWithValue("$path",path);target.Parameters.AddWithValue("$id",move.EntryId);if(target.ExecuteScalar() is string occupied)RetireTree(c,t,root,occupied);}
        TreeIds(c,t,root,move.EntryId);
        RememberTreeVersions(c,t,root);
        DirectoryIndexer.Execute(c,t,"UPDATE Files SET relative_path=$new||substr(relative_path,length($old)+1),canonical_key=$new||substr(relative_path,length($old)+1),path_sort_key=scan_natural($new||substr(relative_path,length($old)+1)),path_revision=path_revision+1,entry_state=coalesce((SELECT original_state FROM ScanRetiredStates s WHERE s.kind='file' AND s.entry_id=Files.entry_id),entry_state) WHERE root_id=$root AND directory_id IN (SELECT id FROM temp.ScanTreeIds)",("$root",root),("$new",path),("$old",old));
        DirectoryIndexer.Execute(c,t,"UPDATE Directories SET relative_path=$new||substr(relative_path,length($old)+1),canonical_key=$new||substr(relative_path,length($old)+1),path_revision=path_revision+1,parent_id=CASE WHEN directory_id=$id THEN $parent ELSE parent_id END,name=CASE WHEN directory_id=$id THEN $name ELSE name END,entry_state=coalesce((SELECT original_state FROM ScanRetiredStates s WHERE s.kind='directory' AND s.entry_id=Directories.directory_id),entry_state) WHERE root_id=$root AND directory_id IN (SELECT id FROM temp.ScanTreeIds)",("$root",root),("$new",path),("$old",old),("$id",move.EntryId),("$parent",parent),("$name",Path.GetFileName(path)));
        DirectoryIndexer.Execute(c,t,"DELETE FROM ScanRetiredStates WHERE (kind='directory' AND entry_id IN (SELECT id FROM temp.ScanTreeIds)) OR (kind='file' AND entry_id IN (SELECT entry_id FROM Files WHERE root_id=$root AND directory_id IN (SELECT id FROM temp.ScanTreeIds)))",("$root",root));
        DirectoryIndexer.Execute(c,t,"UPDATE temp.ScanQueue SET relative_path=$new||substr(relative_path,length($old)+1) WHERE directory_id IN (SELECT directory_id FROM Directories WHERE root_id=$root) AND (relative_path=$old OR substr(relative_path,1,length($prefix))=$prefix)",("$root",root),("$new",path),("$old",old),("$prefix",prefix));
    }
}
