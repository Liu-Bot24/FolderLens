using Microsoft.Data.Sqlite;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed partial class CatalogStore
{
    // The attached file is the durable authority. Each transaction changes its
    // playlist atomically; the main database is disposable and rebuilt on restart.
    private static int InitializePlaylist(SqliteConnection c,string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Execute(c,"ATTACH DATABASE $path AS playlist",("$path",path));
        Execute(c,"PRAGMA playlist.journal_mode=WAL; PRAGMA playlist.synchronous=FULL; PRAGMA playlist.journal_size_limit=1048576;");
        Execute(c,"""
            CREATE TABLE IF NOT EXISTS playlist.PlaylistInfo(version INTEGER NOT NULL,legacy_imported INTEGER NOT NULL);
            INSERT INTO playlist.PlaylistInfo SELECT 1,0 WHERE NOT EXISTS(SELECT 1 FROM playlist.PlaylistInfo);
            CREATE TABLE IF NOT EXISTS playlist.SavedCollections(collection_id TEXT PRIMARY KEY,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,created_utc_ticks INTEGER NOT NULL) STRICT;
            CREATE TABLE IF NOT EXISTS playlist.SavedLinks(collection_id TEXT NOT NULL REFERENCES SavedCollections(collection_id) ON DELETE CASCADE,
                anchor TEXT NOT NULL,directory_identity TEXT NOT NULL,location_key TEXT NOT NULL,path TEXT NOT NULL,added_utc_ticks INTEGER NOT NULL,
                PRIMARY KEY(collection_id,anchor,directory_identity,location_key)) STRICT;
            CREATE INDEX IF NOT EXISTS playlist.IX_SavedLinks_Location ON SavedLinks(anchor,directory_identity,location_key);
            """);
        if(Scalar(c,"SELECT version FROM playlist.PlaylistInfo")==1)
        {
            using var upgrade=c.BeginTransaction();
            DirectoryIndexer.Execute(c,upgrade,"ALTER TABLE playlist.SavedLinks ADD COLUMN file_identity TEXT; ALTER TABLE playlist.SavedLinks ADD COLUMN case_mode TEXT NOT NULL DEFAULT 'unknown'; UPDATE playlist.PlaylistInfo SET version=2;");
            upgrade.Commit();
        }
        if(Scalar(c,"SELECT version FROM playlist.PlaylistInfo")!=2)throw new InvalidDataException("收藏库由较新版本创建，请使用匹配版本。");
        Execute(c,"INSERT INTO main.Collections SELECT * FROM playlist.SavedCollections;");
        return Execute(c,"""
            CREATE TEMP TRIGGER Playlist_Create AFTER INSERT ON main.Collections BEGIN
                INSERT INTO SavedCollections VALUES(NEW.collection_id,NEW.name,NEW.name_key,NEW.created_utc_ticks);
            END;
            CREATE TEMP TRIGGER Playlist_Rename AFTER UPDATE ON main.Collections BEGIN
                UPDATE SavedCollections SET name=NEW.name,name_key=NEW.name_key WHERE collection_id=OLD.collection_id;
            END;
            CREATE TEMP TRIGGER Playlist_Delete AFTER DELETE ON main.Collections BEGIN
                DELETE FROM SavedCollections WHERE collection_id=OLD.collection_id;
            END;
            CREATE TEMP TRIGGER Playlist_Add AFTER INSERT ON main.CollectionMembers BEGIN
                INSERT OR IGNORE INTO SavedLinks(collection_id,anchor,directory_identity,location_key,path,added_utc_ticks,file_identity,case_mode)
                SELECT NEW.collection_id,coalesce(l.anchor_locator,''),coalesce(l.directory_identity,''),NEW.location_key,
                    r.display_path||CASE WHEN substr(r.display_path,-1)='\' THEN '' ELSE '\' END||f.relative_path,NEW.added_utc_ticks,
                    coalesce(f.physical_identity,h.physical_identity),d.case_mode
                FROM Files f JOIN Roots r ON r.root_id=f.root_id JOIN Directories d ON d.directory_id=f.directory_id
                LEFT JOIN CollectionIdentityHistory h ON h.entry_id=f.entry_id
                JOIN DirectoryLocations l ON l.location_id=NEW.directory_location_id WHERE f.entry_id=NEW.entry_id;
            END;
            CREATE TEMP TRIGGER Playlist_Remove AFTER DELETE ON main.CollectionMembers BEGIN
                DELETE FROM SavedLinks WHERE collection_id=OLD.collection_id AND location_key=OLD.location_key
                    AND (anchor,directory_identity)=(SELECT coalesce(anchor_locator,''),coalesce(directory_identity,'') FROM DirectoryLocations WHERE location_id=OLD.directory_location_id);
            END;
            CREATE TEMP TRIGGER Playlist_Observe AFTER UPDATE OF location_key ON main.Files WHEN NEW.entry_state='present' BEGIN
                INSERT OR IGNORE INTO CollectionMembers(collection_id,location_key,entry_id,added_utc_ticks,directory_location_id)
                SELECT p.collection_id,NEW.location_key,NEW.entry_id,p.added_utc_ticks,l.location_id
                FROM DirectoryLocationBindings b JOIN DirectoryLocations l ON l.location_id=b.location_id
                JOIN SavedLinks p ON p.anchor=l.anchor_locator AND p.directory_identity=l.directory_identity AND p.location_key=NEW.location_key
                WHERE b.directory_id=NEW.directory_id AND l.state='active';
            END;
            """);
    }

    public Task ImportLegacyPlaylists(string legacyPath,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        if(playlistPath is null||Scalar(c,"SELECT legacy_imported FROM playlist.PlaylistInfo")!=0)return 0;
        using var transaction=c.BeginTransaction();
        if(File.Exists(legacyPath))
        {
            using var legacy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=legacyPath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());legacy.Open();
            using var read=legacy.CreateCommand();read.CommandText="PRAGMA user_version";
            if((long)read.ExecuteScalar()! !=7)throw new InvalidDataException("旧收藏库版本不匹配，已保留原数据，未进行扫描库升级。");
            using var stable=legacy.BeginTransaction(deferred:true);read.Transaction=stable;
            read.CommandText="SELECT collection_id,name,name_key,created_utc_ticks FROM Collections";
            using(var rows=read.ExecuteReader())while(rows.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                DirectoryIndexer.Execute(c,transaction,"INSERT OR IGNORE INTO Collections VALUES($id,$name,$key,$time)",("$id",rows.GetString(0)),("$name",rows.GetString(1)),("$key",rows.GetString(2)),("$time",rows.GetInt64(3)));
            }
            read.CommandText="""
                SELECT m.collection_id,coalesce(l.anchor_locator,''),coalesce(l.directory_identity,''),m.location_key,r.display_path,f.relative_path,m.added_utc_ticks,coalesce(f.physical_identity,h.physical_identity),d.case_mode
                FROM CollectionMembers m JOIN Files f ON f.entry_id=m.entry_id JOIN Roots r ON r.root_id=f.root_id
                JOIN Directories d ON d.directory_id=f.directory_id LEFT JOIN CollectionIdentityHistory h ON h.entry_id=f.entry_id
                JOIN DirectoryLocations l ON l.location_id=m.directory_location_id WHERE f.entry_state<>'missing' AND l.state='active'
                """;
            using(var rows=read.ExecuteReader())while(rows.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                DirectoryIndexer.Execute(c,transaction,"INSERT OR IGNORE INTO playlist.SavedLinks VALUES($id,$anchor,$identity,$key,$path,$time,$file,$case)",
                    ("$id",rows.GetString(0)),("$anchor",rows.GetString(1)),("$identity",rows.GetString(2)),("$key",rows.GetString(3)),("$path",Path.Combine(rows.GetString(4),rows.GetString(5))),("$time",rows.GetInt64(6)),("$file",rows.GetValue(7)),("$case",rows.GetString(8)));
            }
            stable.Commit();
        }
        DirectoryIndexer.Execute(c,transaction,"UPDATE playlist.PlaylistInfo SET legacy_imported=1");transaction.Commit();return 0;
    },cancellation);

    // Only explicitly saved paths are probed. Opening a playlist never scans all
    // their containing directories, and never imports historical file metadata.
    internal Func<string,CancellationToken,Task<ScanDirectoryPacket>>? PlaylistProbeOverride {get;set;}
    public async Task RefreshPlaylist(string collectionId,CancellationToken cancellation=default)
    {
        if(playlistPath is null)return;
        await using var worker=ScanWorkerClient.FindExecutable() is {} executable?new ScanWorkerClient(executable):null;
        var missingProof=new ScanRenames(this,worker);
        long offset=0;
        while(true)
        {
            var links=await writer.Execute(c=>
            {
                using var cmd=c.CreateCommand();cmd.CommandText="SELECT rowid,path,anchor,directory_identity,location_key,file_identity,case_mode FROM playlist.SavedLinks WHERE collection_id=$id AND rowid>$after ORDER BY rowid LIMIT 64";
                cmd.Parameters.AddWithValue("$id",collectionId);cmd.Parameters.AddWithValue("$after",offset);
                using var rows=cmd.ExecuteReader();var batch=new List<(long Id,string Path,string Anchor,string Identity,string Key,string? FileIdentity,string CaseMode)>();
                while(rows.Read())batch.Add((rows.GetInt64(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.IsDBNull(5)?null:rows.GetString(5),rows.GetString(6)));return batch;
            },cancellation).ConfigureAwait(false);
            if(links.Count==0)break;
            foreach(var link in links)
            {
                cancellation.ThrowIfCancellationRequested();offset=link.Id;
                string path=PathRules.ValidateSource(link.Path),parent=Path.GetDirectoryName(path)!;
                async Task<ScanDirectoryPacket> Probe(string value)=>PlaylistProbeOverride is {} probe?await probe(value,cancellation).ConfigureAwait(false):worker is null?await Task.Run(()=>ScanPathProbe.Read(value),cancellation).ConfigureAwait(false):await worker.Probe(value,cancellation).ConfigureAwait(false);
                var directory=await Probe(parent).ConfigureAwait(false);
                if(directory.State=="missing"&&await missingProof.ConfirmMissingLocation(link.Anchor,link.Identity,cancellation).ConfigureAwait(false))
                {
                    await RemoveSavedLink(collectionId,link.Id,link.Anchor,link.Identity,link.Key,cancellation).ConfigureAwait(false);continue;
                }
                // Unknown/offline is not deletion. A verified same-volume parent
                // and a changed identity/position or missing child is conclusive.
                if(directory.State!="present"||directory.FileStamp is not null||directory.PhysicalIdentity is null||directory.ResolvedLocation is null)continue;
                bool sameVolume=link.Identity.Length>0&&directory.VolumeIdentity is {} volume&&link.Identity.StartsWith(volume+":",StringComparison.Ordinal);
                if(!sameVolume)continue;
                var file=await Probe(path).ConfigureAwait(false);
                string key=FileLocationKey(parent,Path.GetFileName(path),directory.CaseMode,directory.PhysicalIdentity,file.PhysicalIdentity,"");
                bool changed=directory.PhysicalIdentity!=link.Identity||directory.ResolvedLocation!=link.Anchor||file.State=="missing"||
                    file.State=="present"&&file.ResolvedLocation is not null&&file.ResolvedLocation!=Path.Combine(link.Anchor,Path.GetFileName(link.Path))||
                    file.State=="present"&&file.PhysicalIdentity is not null&&link.FileIdentity is not null&&file.PhysicalIdentity!=link.FileIdentity;
                if(changed)
                {
                    await RemoveSavedLink(collectionId,link.Id,link.Anchor,link.Identity,link.Key,cancellation).ConfigureAwait(false);
                    continue;
                }
                if(file.State!="present"||file.FileStamp is null||file.PhysicalIdentity is null||directory.CaseMode is not ("sensitive" or "insensitive"))continue;
                // V1 mappings lack a separate identity. Only a matching old digest
                // proves continuity; a mismatch is uncertainty, never replacement.
                if(link.FileIdentity is null&&link.Key!=FileLocationKey(parent,Path.GetFileName(path),"sensitive",directory.PhysicalIdentity,file.PhysicalIdentity,"")&&
                    link.Key!=FileLocationKey(parent,Path.GetFileName(path),"insensitive",directory.PhysicalIdentity,file.PhysicalIdentity,""))continue;
                await writer.Execute(c=>
                {
                    using var t=c.BeginTransaction();
                    DirectoryIndexer.Execute(c,t,"UPDATE OR IGNORE playlist.SavedLinks SET file_identity=$file,case_mode=$case,location_key=$new WHERE rowid=$row AND collection_id=$id AND location_key=$old",
                        ("$file",file.PhysicalIdentity),("$case",directory.CaseMode),("$new",key),("$row",link.Id),("$id",collectionId),("$old",link.Key));
                    t.Commit();return 0;
                },cancellation).ConfigureAwait(false);
                await ObservePlaylistFile(parent,Path.GetFileName(path),directory,file,cancellation).ConfigureAwait(false);
            }
        }
    }

    private Task RemoveSavedLink(string collectionId,long row,string anchor,string identity,string key,CancellationToken cancellation)=>writer.Execute(c=>
    {
        using var t=c.BeginTransaction();DirectoryIndexer.Execute(c,t,"DELETE FROM CollectionMembers WHERE collection_id=$id AND location_key=$key AND directory_location_id IN(SELECT location_id FROM DirectoryLocations WHERE anchor_locator=$anchor AND directory_identity=$identity)",("$id",collectionId),("$key",key),("$anchor",anchor),("$identity",identity));
        DirectoryIndexer.Execute(c,t,"DELETE FROM playlist.SavedLinks WHERE rowid=$row AND collection_id=$id AND anchor=$anchor AND directory_identity=$identity AND location_key=$key",("$row",row),("$id",collectionId),("$anchor",anchor),("$identity",identity),("$key",key));t.Commit();return 0;
    },cancellation);

    private Task ObservePlaylistFile(string parent,string name,ScanDirectoryPacket directory,ScanDirectoryPacket file,CancellationToken cancellation)=>writer.Execute(c=>
    {
        string root="playlist:"+DirectoryIndexer.StablePathId(parent,""),dir=DirectoryIndexer.StablePathId(root,"");
        using var t=c.BeginTransaction();
        DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO Roots(root_id,display_path,canonical_key,root_epoch,availability,scan_state,volume_identity) VALUES($root,$path,$root,1,'online','partial',$identity)",("$root",root),("$path",parent),("$identity",directory.PhysicalIdentity));
        DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES($dir,$root,'','','',$case)",("$dir",dir),("$root",root),("$case",directory.CaseMode));
        DirectoryIndexer.Execute(c,t,"UPDATE Directories SET case_mode=$case WHERE directory_id=$dir",("$case",directory.CaseMode),("$dir",dir));
        BindDirectoryLocation(c,t,dir,directory.PhysicalIdentity,directory.ResolvedLocation);
        string entry=DirectoryIndexer.StablePathId(root,name);var stamp=file.FileStamp!.Value;
        DirectoryIndexer.Execute(c,t,"""
            INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,mtime_utc_ticks,physical_identity,updated_revision)
            VALUES($id,$root,$dir,$name,$ext,$name,$name,$sort,$sort,1,$signature,$kind,'extension',$bytes,$time,$identity,0)
            ON CONFLICT(entry_id) DO UPDATE SET logical_bytes=excluded.logical_bytes,mtime_utc_ticks=excluded.mtime_utc_ticks,physical_identity=excluded.physical_identity,entry_state='present',
                file_version=Files.file_version+CASE WHEN Files.stat_signature<>excluded.stat_signature THEN 1 ELSE 0 END,stat_signature=excluded.stat_signature
            """,("$id",entry),("$root",root),("$dir",dir),("$name",name),("$ext",Path.GetExtension(name).ToLowerInvariant()),("$sort",NaturalOrder.Key(name)),("$signature",$"{stamp.Length}:{stamp.ModifiedUtcTicks}:{file.PhysicalIdentity}"),("$kind",FileKinds.Candidate(name)),("$bytes",stamp.Length),("$time",stamp.ModifiedUtcTicks),("$identity",file.PhysicalIdentity));
        // Existing matching entries also restore membership after a new tag was added.
        DirectoryIndexer.Execute(c,t,"UPDATE Files SET location_key=location_key WHERE entry_id=$id",("$id",entry));
        t.Commit();return 0;
    },cancellation);
}
