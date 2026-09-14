using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed partial class CatalogStore
{
    internal static string FileLocationKey(string root,string relative,string mode,string? parentIdentity,string? fileIdentity,string entryId)
    {
        // Parent identity resolves every ancestor component without guessing its
        // case mode. Keep the leaf name so distinct hard-link paths stay distinct.
        // Include file identity: a different occupant of a path is not the old file.
        string leaf=Path.GetFileName(relative.Replace('/', '\\'));
        if(!string.IsNullOrEmpty(parentIdentity)&&!string.IsNullOrEmpty(fileIdentity))
            return Digest(new[]{"file-location-v5",parentIdentity,mode=="insensitive"?leaf.ToUpperInvariant():leaf,fileIdentity});
        // Unknown identity never grants permission to merge two references.
        return Digest(new[]{"unresolved-location-v5",entryId,Path.Combine(root,relative)});
    }
    private static string Digest(string[] parts)=>"v5:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    private const string FileLocationExpression="""
        (SELECT lens_file_location(r.display_path,Files.relative_path,d.case_mode,
            coalesce(p.physical_identity,CASE WHEN d.relative_path='' THEN r.volume_identity END),coalesce(Files.physical_identity,h.physical_identity),Files.entry_id)
         FROM Roots r JOIN Directories d ON d.directory_id=Files.directory_id
         LEFT JOIN CollectionIdentityHistory h ON h.entry_id=Files.entry_id
         LEFT JOIN ScanDirectoryIdentities p ON p.directory_id=d.directory_id WHERE r.root_id=Files.root_id)
        """;
    private static string LocationTriggers()=>$$"""
        CREATE TRIGGER Files_Location_Insert AFTER INSERT ON Files BEGIN
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE entry_id=NEW.entry_id;
        END;
        CREATE TRIGGER Files_CollectionsMissing AFTER UPDATE OF entry_state ON Files WHEN NEW.entry_state='missing' AND OLD.entry_state<>'missing' BEGIN
            DELETE FROM CollectionMembers WHERE location_key=OLD.location_key;
        END;
        {{FileLocationUpdateTrigger}}
        CREATE TRIGGER Roots_Location_Update AFTER UPDATE OF display_path,volume_identity ON Roots
        WHEN OLD.display_path<>NEW.display_path OR OLD.volume_identity IS NOT NEW.volume_identity BEGIN
            DELETE FROM CollectionMembers WHERE (OLD.display_path<>NEW.display_path OR (OLD.volume_identity IS NOT NULL AND NEW.volume_identity IS NOT NULL AND OLD.volume_identity<>NEW.volume_identity)) AND location_key IN(SELECT location_key FROM Files WHERE root_id=NEW.root_id);
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE root_id=NEW.root_id;
        END;
        CREATE TRIGGER Directories_Location_Update AFTER UPDATE OF case_mode ON Directories WHEN OLD.case_mode<>NEW.case_mode BEGIN
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE root_id=NEW.root_id AND directory_id=NEW.directory_id;
        END;
        CREATE TRIGGER DirectoryIdentity_Location_Insert AFTER INSERT ON ScanDirectoryIdentities BEGIN
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE root_id=(SELECT root_id FROM Directories WHERE directory_id=NEW.directory_id) AND directory_id=NEW.directory_id;
        END;
        CREATE TRIGGER DirectoryIdentity_Location_Update AFTER UPDATE OF physical_identity ON ScanDirectoryIdentities WHEN OLD.physical_identity<>NEW.physical_identity BEGIN
            DELETE FROM CollectionMembers WHERE location_key IN(SELECT location_key FROM Files WHERE root_id=(SELECT root_id FROM Directories WHERE directory_id=NEW.directory_id) AND directory_id=NEW.directory_id);
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE root_id=(SELECT root_id FROM Directories WHERE directory_id=NEW.directory_id) AND directory_id=NEW.directory_id;
        END;
        CREATE TRIGGER CollectionMembers_RefreshIdentity AFTER UPDATE OF location_key ON Files WHEN OLD.location_key<>NEW.location_key BEGIN
            INSERT OR IGNORE INTO CollectionMembers(collection_id,location_key,entry_id,added_utc_ticks)
                SELECT collection_id,NEW.location_key,NEW.entry_id,added_utc_ticks FROM CollectionMembers WHERE location_key=OLD.location_key;
            DELETE FROM CollectionMembers WHERE location_key=OLD.location_key;
        END;
        """;
    private static string FileLocationUpdateTrigger=>$$"""
        CREATE TRIGGER Files_Location_Update AFTER UPDATE OF relative_path,root_id,directory_id,physical_identity ON Files
        WHEN OLD.relative_path IS NOT NEW.relative_path OR OLD.root_id IS NOT NEW.root_id OR OLD.directory_id IS NOT NEW.directory_id OR OLD.physical_identity IS NOT NEW.physical_identity BEGIN
            INSERT INTO CollectionIdentityHistory(entry_id,physical_identity)
                SELECT OLD.entry_id,OLD.physical_identity WHERE OLD.physical_identity IS NOT NULL AND NEW.physical_identity IS NULL
                ON CONFLICT(entry_id) DO UPDATE SET physical_identity=excluded.physical_identity;
            DELETE FROM CollectionMembers WHERE location_key=OLD.location_key AND
                (OLD.relative_path<>NEW.relative_path OR OLD.root_id<>NEW.root_id OR
                 (NEW.physical_identity IS NOT NULL AND coalesce(OLD.physical_identity,(SELECT physical_identity FROM CollectionIdentityHistory WHERE entry_id=OLD.entry_id))<>NEW.physical_identity));
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE entry_id=NEW.entry_id;
            DELETE FROM CollectionIdentityHistory WHERE entry_id=NEW.entry_id AND NEW.physical_identity IS NOT NULL;
        END;
        """;
    private const string IdentityHistoryTable="CREATE TABLE IF NOT EXISTS CollectionIdentityHistory(entry_id TEXT PRIMARY KEY REFERENCES Files(entry_id) ON DELETE CASCADE,physical_identity TEXT NOT NULL) STRICT;";
    private static void MigrateCollectionIdentityHistory(SqliteConnection connection,CancellationToken cancellation,Action<string>? progress)
    {
        var timer=Stopwatch.StartNew();int reported=-1;
        progress?.Invoke("正在备份本地索引。");
        using(var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=connection.DataSource+".pre-v6-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".bak",Pooling=false}.ToString()))
        {
            backup.Open();CatalogBackup.Copy(connection,backup,cancellation,(done,total)=>
            {
                int percent=total==0?100:(int)((long)done*100/total);
                if(percent!=reported){reported=percent;progress?.Invoke($"正在备份本地索引 · {percent}%");}
            });
        }
        OperationMigrationMeasured?.Invoke("identityHistoryBackup",timer.Elapsed.TotalMilliseconds);timer.Restart();
        progress?.Invoke("正在更新本地收藏索引。");
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText=IdentityHistoryTable+"""
            DROP TRIGGER Files_Location_Insert;DROP TRIGGER Files_CollectionsMissing;DROP TRIGGER Files_Location_Update;
            DROP TRIGGER Roots_Location_Update;DROP TRIGGER Directories_Location_Update;
            DROP TRIGGER DirectoryIdentity_Location_Insert;DROP TRIGGER DirectoryIdentity_Location_Update;DROP TRIGGER CollectionMembers_RefreshIdentity;
            """+LocationTriggers()+"DELETE FROM CollectionMembers WHERE EXISTS(SELECT 1 FROM Files f WHERE f.entry_id=CollectionMembers.entry_id AND f.entry_state='missing');UPDATE SchemaInfo SET schema_version=6;PRAGMA user_version=6;";
        command.ExecuteNonQuery();cancellation.ThrowIfCancellationRequested();transaction.Commit();
        OperationMigrationMeasured?.Invoke("identityHistoryMigration",timer.Elapsed.TotalMilliseconds);
    }
    private static void RepairLocationUpdateTrigger(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='Files_Location_Update'";
        if(string.Equals((string?)command.ExecuteScalar(),FileLocationUpdateTrigger.TrimEnd(';'),StringComparison.Ordinal))return;
        using var transaction=connection.BeginTransaction();command.Transaction=transaction;
        command.CommandText="DROP TRIGGER IF EXISTS Files_Location_Update;"+FileLocationUpdateTrigger;
        command.ExecuteNonQuery();transaction.Commit();
    }
    private static void MigrateCollectionLocations(SqliteConnection connection,bool existing,CancellationToken cancellation,Action<string>? progress)
    {
        var timer=Stopwatch.StartNew();
        if(existing)
        {
            using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=connection.DataSource+".pre-v5-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".bak",Pooling=false}.ToString());
            backup.Open();int reported=-1;
            CatalogBackup.Copy(connection,backup,cancellation,(done,total)=>
            {
                int percent=total==0?100:(int)((long)done*100/total);
                if(percent!=reported){reported=percent;progress?.Invoke($"正在备份本地索引 · {percent}%");}
            });
        }
        OperationMigrationMeasured?.Invoke("backup",timer.Elapsed.TotalMilliseconds);timer.Restart();
        progress?.Invoke("正在更新本地索引，首次升级可能需要几分钟。");
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText=$$"""
            CREATE TABLE IF NOT EXISTS ScanDirectoryIdentities(directory_id TEXT PRIMARY KEY REFERENCES Directories(directory_id) ON DELETE CASCADE,physical_identity TEXT NOT NULL);
            {{IdentityHistoryTable}}
            DROP TRIGGER CollectionMembers_FollowKnownRename;
            DROP TRIGGER Files_Location_Insert;DROP TRIGGER Files_Location_Update;DROP TRIGGER Roots_Location_Update;DROP TRIGGER Directories_Location_Update;
            DROP INDEX IX_Files_Location;
            UPDATE Files SET location_key={{FileLocationExpression}};
            CREATE INDEX IX_Files_Location ON Files(location_key,entry_state,entry_id);
            CREATE TEMP TABLE SavedCollectionLocations AS SELECT m.collection_id,coalesce(f.location_key,m.location_key) location_key,m.entry_id,m.added_utc_ticks FROM CollectionMembers m LEFT JOIN Files f ON f.entry_id=m.entry_id WHERE f.entry_state IS NOT 'missing';
            DELETE FROM CollectionMembers;
            INSERT INTO CollectionMembers SELECT collection_id,location_key,min(entry_id),min(added_utc_ticks) FROM temp.SavedCollectionLocations GROUP BY collection_id,location_key;
            DROP TABLE temp.SavedCollectionLocations;
            {{LocationTriggers()}}
            UPDATE SchemaInfo SET schema_version=6;
            PRAGMA user_version=6;
            """;
        command.ExecuteNonQuery();cancellation.ThrowIfCancellationRequested();transaction.Commit();
        OperationMigrationMeasured?.Invoke("locationMigration",timer.Elapsed.TotalMilliseconds);
    }
    public static Action<string,double>? OperationMigrationMeasured {get;set;}
}
