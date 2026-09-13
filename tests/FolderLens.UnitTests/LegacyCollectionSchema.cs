using Microsoft.Data.Sqlite;

namespace FolderLens.UnitTests;

internal static class LegacyCollectionSchema
{
    // Build actual pre-v4 fixtures, rather than relabelling a v4 schema as legacy.
    internal static void Catalog(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="""
            DROP TRIGGER CollectionMembers_FollowKnownRename;
            DROP TRIGGER Files_Location_Insert;DROP TRIGGER Files_Location_Update;DROP TRIGGER Roots_Location_Update;DROP TRIGGER Directories_Location_Update;
            DROP TABLE CollectionMembers;DROP TABLE Collections;DROP INDEX IX_Files_Location;ALTER TABLE Files DROP COLUMN location_key;
            """;command.ExecuteNonQuery();
    }
    internal static void Sessions(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();command.CommandText="ALTER TABLE ResultItems DROP COLUMN source_root_id;ALTER TABLE ResultItems DROP COLUMN source_root_path;ALTER TABLE ResultItems DROP COLUMN source_root_epoch;";command.ExecuteNonQuery();
    }
}
