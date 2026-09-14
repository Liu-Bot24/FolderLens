using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed partial class CatalogStore
{
    internal const string LocationBindingJoin="JOIN DirectoryLocationBindings lb ON lb.directory_id=f.directory_id JOIN DirectoryLocations dl ON dl.location_id=lb.location_id";
    internal static string DirectoryLocation(string alias)=>$"(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id={alias}.directory_id)";
    private static void MigrateDirectoryLocations(SqliteConnection c,bool existing,CancellationToken cancellation,Action<string>? progress,SqliteTransaction? transaction=null)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        progress?.Invoke("正在更新本地收藏索引。");
        using var owned=transaction is null?c.BeginTransaction():null;
        var t=transaction??owned!;using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="""
            CREATE TABLE DirectoryLocations(location_id TEXT PRIMARY KEY,directory_identity TEXT,anchor_locator TEXT,locator_kind TEXT NOT NULL DEFAULT 'isolated',state TEXT NOT NULL DEFAULT 'active' CHECK(state IN('active','retired'))) STRICT;
            CREATE INDEX IX_DirectoryLocations_Locator ON DirectoryLocations(anchor_locator,directory_identity,state);
            CREATE TABLE DirectoryLocationBindings(directory_id TEXT PRIMARY KEY REFERENCES Directories(directory_id) ON DELETE CASCADE,location_id TEXT NOT NULL REFERENCES DirectoryLocations(location_id),binding_revision INTEGER NOT NULL DEFAULT 1,proof_kind TEXT NOT NULL DEFAULT 'isolated') STRICT;
            CREATE INDEX IX_DirectoryLocationBindings_Location ON DirectoryLocationBindings(location_id,directory_id);
            INSERT INTO DirectoryLocations(location_id,directory_identity) SELECT d.directory_id,coalesce(i.physical_identity,CASE WHEN d.relative_path='' THEN r.volume_identity END) FROM Directories d JOIN Roots r ON r.root_id=d.root_id LEFT JOIN ScanDirectoryIdentities i ON i.directory_id=d.directory_id;
            INSERT INTO DirectoryLocationBindings(directory_id,location_id) SELECT directory_id,directory_id FROM Directories;
            DROP TRIGGER IF EXISTS CollectionAliases_Invalidate;
            DROP TRIGGER IF EXISTS Files_Location_Insert;DROP TRIGGER IF EXISTS Files_CollectionsMissing;DROP TRIGGER IF EXISTS Files_Location_Update;
            DROP TRIGGER IF EXISTS Roots_Location_Update;DROP TRIGGER IF EXISTS Directories_Location_Update;
            DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Insert;DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Update;DROP TRIGGER IF EXISTS CollectionMembers_RefreshIdentity;
            CREATE TABLE NewCollectionMembers(collection_id TEXT NOT NULL REFERENCES Collections(collection_id) ON DELETE CASCADE,location_key TEXT NOT NULL,entry_id TEXT NOT NULL,added_utc_ticks INTEGER NOT NULL,directory_location_id TEXT NOT NULL REFERENCES DirectoryLocations(location_id),PRIMARY KEY(collection_id,directory_location_id,location_key)) STRICT;
            INSERT INTO NewCollectionMembers SELECT m.collection_id,f.location_key,m.entry_id,m.added_utc_ticks,b.location_id FROM CollectionMembers m JOIN Files f ON f.entry_id=m.entry_id JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id WHERE f.entry_state<>'missing';
            DROP TABLE CollectionMembers;
            ALTER TABLE NewCollectionMembers RENAME TO CollectionMembers;
            CREATE INDEX IX_CollectionMembers_Location ON CollectionMembers(directory_location_id,location_key,collection_id);
            """+PositionLocationTriggers()+"UPDATE SchemaInfo SET schema_version=7;PRAGMA user_version=7;";
        cmd.ExecuteNonQuery();OperationMigrationMeasured?.Invoke("directoryLocationPrepared",timer.Elapsed.TotalMilliseconds);cancellation.ThrowIfCancellationRequested();owned?.Commit();
    }

    private static string PositionLocationTriggers()
    {
        string oldLocation=DirectoryLocation("OLD"),newLocation=DirectoryLocation("NEW");
        // These legacy expressions remain only for the physical digest. All tag
        // operations below also constrain the persistent directory position.
        string sql=LocationTriggers()
            .Replace("WHERE location_key=OLD.location_key",$"WHERE directory_location_id={oldLocation} AND location_key=OLD.location_key",StringComparison.Ordinal)
            .Replace("INSERT OR IGNORE INTO CollectionMembers(collection_id,location_key,entry_id,added_utc_ticks)","INSERT OR IGNORE INTO CollectionMembers(collection_id,location_key,entry_id,added_utc_ticks,directory_location_id)",StringComparison.Ordinal)
            .Replace("SELECT collection_id,NEW.location_key,NEW.entry_id,added_utc_ticks FROM",$"SELECT collection_id,NEW.location_key,NEW.entry_id,added_utc_ticks,{newLocation} FROM",StringComparison.Ordinal);
        // Root and directory replacement retire position instances explicitly.
        int start=sql.IndexOf("CREATE TRIGGER Roots_Location_Update",StringComparison.Ordinal);
        int end=sql.IndexOf("CREATE TRIGGER Directories_Location_Update",start,StringComparison.Ordinal);
        sql=sql[..start]+$$"""
            CREATE TRIGGER Roots_Location_Update AFTER UPDATE OF display_path,volume_identity ON Roots
            WHEN OLD.display_path<>NEW.display_path OR OLD.volume_identity IS NOT NEW.volume_identity BEGIN
                UPDATE DirectoryLocations SET state='retired' WHERE location_id IN(SELECT b.location_id FROM DirectoryLocationBindings b JOIN Directories d ON d.directory_id=b.directory_id WHERE d.root_id=NEW.root_id)
                    AND (OLD.display_path<>NEW.display_path OR (OLD.volume_identity IS NOT NULL AND NEW.volume_identity IS NOT NULL AND OLD.volume_identity<>NEW.volume_identity));
                UPDATE Files SET location_key={{FileLocationExpression}} WHERE root_id=NEW.root_id;
            END;

            """+sql[end..];
        sql=sql.Replace("DELETE FROM CollectionMembers WHERE location_key IN(SELECT location_key FROM Files WHERE root_id=(SELECT root_id FROM Directories WHERE directory_id=NEW.directory_id) AND directory_id=NEW.directory_id);",
            "UPDATE DirectoryLocations SET state='retired' WHERE location_id=(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=NEW.directory_id);",StringComparison.Ordinal);
        return sql+$$"""

            CREATE TRIGGER DirectoryLocation_Insert AFTER INSERT ON Directories BEGIN
                INSERT INTO DirectoryLocations(location_id) VALUES(lower(hex(randomblob(16))));
                INSERT INTO DirectoryLocationBindings(directory_id,location_id) SELECT NEW.directory_id,location_id FROM DirectoryLocations WHERE rowid=last_insert_rowid();
            END;
            CREATE TRIGGER DirectoryLocation_Retire AFTER UPDATE OF relative_path,root_id,entry_state ON Directories
            WHEN OLD.relative_path<>NEW.relative_path OR OLD.root_id<>NEW.root_id OR (OLD.entry_state<>'missing' AND NEW.entry_state='missing') BEGIN
                UPDATE DirectoryLocations SET state='retired' WHERE location_id=(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=OLD.directory_id);
            END;
            CREATE TRIGGER DirectoryLocation_Retired AFTER UPDATE OF state ON DirectoryLocations WHEN OLD.state='active' AND NEW.state='retired' BEGIN
                DELETE FROM CollectionMembers WHERE directory_location_id=OLD.location_id;
                UPDATE DirectoryLocationBindings SET binding_revision=binding_revision+1 WHERE location_id=OLD.location_id;
            END;
            CREATE TRIGGER CollectionAliases_Invalidate BEFORE UPDATE OF entry_state,relative_path,root_id,directory_id,physical_identity ON Files
            WHEN (NEW.entry_state='missing' AND OLD.entry_state<>'missing') OR OLD.relative_path<>NEW.relative_path OR OLD.root_id<>NEW.root_id OR
                (NEW.physical_identity IS NOT NULL AND coalesce(OLD.physical_identity,(SELECT physical_identity FROM CollectionIdentityHistory WHERE entry_id=OLD.entry_id))<>NEW.physical_identity)
            BEGIN
                UPDATE Files SET entry_state='missing',file_version=file_version+1 WHERE location_key=OLD.location_key AND directory_id IN(SELECT directory_id FROM DirectoryLocationBindings WHERE location_id={{oldLocation}}) AND entry_id<>OLD.entry_id AND entry_state<>'missing';
            END;
            """;
    }

    // Called before publishing a directory's first file batch. The resolved
    // locator comes from the same metadata handle as the physical identity.
    internal static void BindDirectoryLocation(SqliteConnection c,SqliteTransaction t,string directory,string? identity,string? locator)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="SELECT b.location_id,l.state,l.directory_identity,l.anchor_locator FROM DirectoryLocationBindings b JOIN DirectoryLocations l ON l.location_id=b.location_id WHERE b.directory_id=$dir";cmd.Parameters.AddWithValue("$dir",directory);
        string previous,state;string? known,anchor;
        using(var row=cmd.ExecuteReader()){if(!row.Read())throw new InvalidDataException("目录位置绑定缺失。");previous=row.GetString(0);state=row.GetString(1);known=row.IsDBNull(2)?null:row.GetString(2);anchor=row.IsDBNull(3)?null:row.GetString(3);}
        bool changed=state=="retired"||(known is not null&&identity is not null&&known!=identity)||(anchor is not null&&locator is not null&&anchor!=locator);
        if(changed)
        {
            DirectoryIndexer.Execute(c,t,"UPDATE DirectoryLocations SET state='retired' WHERE location_id=$id",("$id",previous));
            string fresh=Guid.NewGuid().ToString("N");DirectoryIndexer.Execute(c,t,"INSERT INTO DirectoryLocations(location_id) VALUES($id);UPDATE DirectoryLocationBindings SET location_id=$id,binding_revision=binding_revision+1,proof_kind='isolated' WHERE directory_id=$dir",("$id",fresh),("$dir",directory));previous=fresh;
        }
        if(identity is null||locator is null)return;
        cmd.CommandText="SELECT location_id FROM DirectoryLocations WHERE anchor_locator=$locator AND directory_identity=$identity AND state='active' AND location_id<>$previous ORDER BY location_id LIMIT 1";
        cmd.Parameters.AddWithValue("$locator",locator);cmd.Parameters.AddWithValue("$identity",identity);cmd.Parameters.AddWithValue("$previous",previous);
        string? equivalent=cmd.ExecuteScalar() as string;
        if(equivalent is not null)
        {
            // Proven same-position bindings merge explicitly; no trigger follows
            // a move. Preserve tags independently added while the relation was unknown.
            DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO CollectionMembers SELECT collection_id,location_key,entry_id,added_utc_ticks,$target FROM CollectionMembers WHERE directory_location_id=$old;DELETE FROM CollectionMembers WHERE directory_location_id=$old;UPDATE DirectoryLocationBindings SET location_id=$target,binding_revision=binding_revision+1,proof_kind='resolved' WHERE location_id=$old;UPDATE DirectoryLocations SET state='retired' WHERE location_id=$old",("$target",equivalent),("$old",previous));
        }
        else DirectoryIndexer.Execute(c,t,"UPDATE DirectoryLocations SET directory_identity=$identity,anchor_locator=$locator,locator_kind='resolved' WHERE location_id=$id",("$identity",identity),("$locator",locator),("$id",previous));
    }
}
