using Microsoft.Data.Sqlite;

namespace FolderLens.UnitTests;

internal static class LegacyCollectionSchema
{
    // Build actual pre-v4 fixtures, rather than relabelling a v4 schema as legacy.
    internal static void Catalog(SqliteConnection connection)
    {
        LegacyDirectoryLocations.Remove(connection);
        using var command=connection.CreateCommand();command.CommandText="""
            DROP TRIGGER IF EXISTS CollectionAliases_Invalidate;
            DROP TRIGGER IF EXISTS CollectionMembers_FollowKnownRename;
            DROP TRIGGER IF EXISTS CollectionMembers_RefreshIdentity;
            DROP TRIGGER IF EXISTS Files_CollectionsMissing;
            DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Insert;
            DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Update;
            DROP TRIGGER IF EXISTS Files_Location_Insert;DROP TRIGGER IF EXISTS Files_Location_Update;DROP TRIGGER IF EXISTS Roots_Location_Update;DROP TRIGGER IF EXISTS Directories_Location_Update;
            DROP TABLE CollectionMembers;DROP TABLE Collections;DROP INDEX IX_Files_Location;ALTER TABLE Files DROP COLUMN location_key;
            """;command.ExecuteNonQuery();
    }
    internal static void V4(SqliteConnection connection)
    {
        LegacyDirectoryLocations.Remove(connection);
        using var command=connection.CreateCommand();command.CommandText="""
            DROP TRIGGER IF EXISTS CollectionAliases_Invalidate;
            DROP TRIGGER IF EXISTS CollectionMembers_RefreshIdentity;DROP TRIGGER IF EXISTS Files_CollectionsMissing;
            DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Insert;DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Update;
            DROP TRIGGER IF EXISTS Files_Location_Insert;DROP TRIGGER IF EXISTS Files_Location_Update;DROP TRIGGER IF EXISTS Roots_Location_Update;DROP TRIGGER IF EXISTS Directories_Location_Update;
            UPDATE Files SET location_key=(SELECT lens_location(r.display_path,Files.relative_path,d.case_mode) FROM Roots r JOIN Directories d ON d.directory_id=Files.directory_id WHERE r.root_id=Files.root_id);
            UPDATE CollectionMembers SET location_key=(SELECT location_key FROM Files WHERE entry_id=CollectionMembers.entry_id);
            CREATE TRIGGER Files_Location_Insert AFTER INSERT ON Files BEGIN
                UPDATE Files SET location_key=(SELECT lens_location(r.display_path,NEW.relative_path,d.case_mode) FROM Roots r JOIN Directories d ON d.directory_id=NEW.directory_id WHERE r.root_id=NEW.root_id) WHERE entry_id=NEW.entry_id;
            END;
            CREATE TRIGGER Files_Location_Update AFTER UPDATE OF relative_path,root_id,directory_id ON Files BEGIN
                UPDATE Files SET location_key=(SELECT lens_location(r.display_path,NEW.relative_path,d.case_mode) FROM Roots r JOIN Directories d ON d.directory_id=NEW.directory_id WHERE r.root_id=NEW.root_id) WHERE entry_id=NEW.entry_id;
            END;
            CREATE TRIGGER Roots_Location_Update AFTER UPDATE OF display_path ON Roots WHEN OLD.display_path<>NEW.display_path BEGIN
                UPDATE Files SET location_key=lens_location(NEW.display_path,relative_path,(SELECT case_mode FROM Directories WHERE directory_id=Files.directory_id)) WHERE root_id=NEW.root_id;
            END;
            CREATE TRIGGER Directories_Location_Update AFTER UPDATE OF case_mode ON Directories WHEN OLD.case_mode<>NEW.case_mode BEGIN
                UPDATE Files SET location_key=lens_location((SELECT display_path FROM Roots WHERE root_id=Files.root_id),relative_path,NEW.case_mode) WHERE directory_id=NEW.directory_id;
            END;
            CREATE TRIGGER CollectionMembers_FollowKnownRename AFTER UPDATE OF location_key ON Files WHEN OLD.location_key<>NEW.location_key BEGIN
                DELETE FROM CollectionMembers WHERE location_key=OLD.location_key AND collection_id IN(SELECT collection_id FROM CollectionMembers WHERE location_key=NEW.location_key);
                UPDATE CollectionMembers SET location_key=NEW.location_key,entry_id=NEW.entry_id WHERE location_key=OLD.location_key;
            END;
            UPDATE SchemaInfo SET schema_version=4;PRAGMA user_version=4;
            """;command.ExecuteNonQuery();
    }
    internal static void Sessions(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="ALTER TABLE ResultItems DROP COLUMN observed_directory_location_id;ALTER TABLE ResultItems DROP COLUMN observed_binding_revision;ALTER TABLE ResultItems DROP COLUMN source_root_id;ALTER TABLE ResultItems DROP COLUMN source_root_path;ALTER TABLE ResultItems DROP COLUMN source_root_epoch;";command.ExecuteNonQuery();
    }
}
