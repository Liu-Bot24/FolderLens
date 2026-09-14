using Microsoft.Data.Sqlite;

namespace FolderLens.UnitTests;

internal static class LegacyDirectoryLocations
{
    // Remove v7 fields and triggers, rather than merely relabelling a v7 catalog.
    internal static void Remove(SqliteConnection c)
    {
        using var command=c.CreateCommand();
        command.CommandText="""
            DROP TRIGGER IF EXISTS DirectoryLocation_Insert;DROP TRIGGER IF EXISTS DirectoryLocation_Retire;DROP TRIGGER IF EXISTS DirectoryLocation_Retired;
            DROP TRIGGER IF EXISTS CollectionAliases_Invalidate;
            DROP TRIGGER IF EXISTS Files_Location_Insert;DROP TRIGGER IF EXISTS Files_CollectionsMissing;DROP TRIGGER IF EXISTS Files_Location_Update;
            DROP TRIGGER IF EXISTS Roots_Location_Update;DROP TRIGGER IF EXISTS Directories_Location_Update;
            DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Insert;DROP TRIGGER IF EXISTS DirectoryIdentity_Location_Update;DROP TRIGGER IF EXISTS CollectionMembers_RefreshIdentity;
            CREATE TABLE OldCollectionMembers(collection_id TEXT NOT NULL REFERENCES Collections(collection_id) ON DELETE CASCADE,location_key TEXT NOT NULL,entry_id TEXT NOT NULL,added_utc_ticks INTEGER NOT NULL,PRIMARY KEY(collection_id,location_key)) STRICT;
            INSERT OR IGNORE INTO OldCollectionMembers SELECT collection_id,location_key,entry_id,added_utc_ticks FROM CollectionMembers;
            DROP TABLE CollectionMembers;ALTER TABLE OldCollectionMembers RENAME TO CollectionMembers;
            CREATE INDEX IX_CollectionMembers_Location ON CollectionMembers(location_key,collection_id);
            DROP TABLE DirectoryLocationBindings;DROP TABLE DirectoryLocations;
            """;
        command.ExecuteNonQuery();
    }
}
