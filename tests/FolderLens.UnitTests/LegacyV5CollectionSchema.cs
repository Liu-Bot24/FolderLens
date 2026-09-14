using Microsoft.Data.Sqlite;

namespace FolderLens.UnitTests;

// Exact v5 expressions/triggers from f05fde5, retained only to test upgrades.
internal static class LegacyV5CollectionSchema
{
    private const string FileLocationExpression="""
        (SELECT lens_file_location(r.display_path,Files.relative_path,d.case_mode,
            coalesce(p.physical_identity,CASE WHEN d.relative_path='' THEN r.volume_identity END),Files.physical_identity,Files.entry_id)
         FROM Roots r JOIN Directories d ON d.directory_id=Files.directory_id
         LEFT JOIN ScanDirectoryIdentities p ON p.directory_id=d.directory_id WHERE r.root_id=Files.root_id)
        """;
    private static string LocationTriggers()=>$$"""
        CREATE TRIGGER Files_Location_Insert AFTER INSERT ON Files BEGIN
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE entry_id=NEW.entry_id;
        END;
        CREATE TRIGGER Files_CollectionsMissing AFTER UPDATE OF entry_state ON Files WHEN NEW.entry_state='missing' AND OLD.entry_state<>'missing' BEGIN
            DELETE FROM CollectionMembers WHERE location_key=OLD.location_key;
        END;
        CREATE TRIGGER Files_Location_Update AFTER UPDATE OF relative_path,root_id,directory_id,physical_identity ON Files BEGIN
            DELETE FROM CollectionMembers WHERE location_key=OLD.location_key AND
                (OLD.relative_path<>NEW.relative_path OR OLD.root_id<>NEW.root_id OR
                 (OLD.physical_identity IS NOT NULL AND NEW.physical_identity IS NOT NULL AND OLD.physical_identity<>NEW.physical_identity));
            UPDATE Files SET location_key={{FileLocationExpression}} WHERE entry_id=NEW.entry_id;
        END;
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
    internal static void Apply(SqliteConnection connection)
    {
        LegacyDirectoryLocations.Remove(connection);using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            DROP TRIGGER IF EXISTS CollectionAliases_Invalidate;
            DROP TRIGGER IF EXISTS Files_Location_Insert;DROP TRIGGER IF EXISTS Files_CollectionsMissing;DROP TRIGGER IF EXISTS Files_Location_Update;
            DROP TRIGGER IF EXISTS Roots_Location_Update;DROP TRIGGER IF EXISTS Directories_Location_Update;DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Insert;
            DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Update;DROP TRIGGER IF EXISTS CollectionMembers_RefreshIdentity;
            DROP TABLE CollectionIdentityHistory;
            """+LocationTriggers()+"UPDATE SchemaInfo SET schema_version=5;PRAGMA user_version=5;";
        command.ExecuteNonQuery();transaction.Commit();
    }
}
