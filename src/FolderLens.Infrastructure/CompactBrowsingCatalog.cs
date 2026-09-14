using Microsoft.Data.Sqlite;
namespace FolderLens.Infrastructure;

public static class CompactBrowsingCatalog
{
    public const long MaximumBytes=2L<<30;
    // Keep the indexed sort prefix. SQL still resolves equal values using the
    // original path key; do not duplicate that long key in six B-trees.
    public static void Configure(SqliteConnection connection,long maximumBytes=MaximumBytes)
    {
        using var command=connection.CreateCommand();command.CommandText="PRAGMA page_size";
        long pageSize=(long)command.ExecuteScalar()!;
        if(maximumBytes<pageSize*256)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using(var transaction=connection.BeginTransaction())
        {
            command.Transaction=transaction;
            foreach(var (name,column) in new[]{("Name","name_sort_key"),("Size","logical_bytes"),("Mtime","mtime_utc_ticks"),("LongEdge","long_edge"),("Pixels","pixel_count"),("Duration","duration_ms")})
            {
                command.CommandText=$"DROP INDEX IF EXISTS IX_Files_{name}; CREATE INDEX IX_Files_{name} ON Files(root_id,entry_state,{column});";command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        command.Transaction=null;command.CommandText=$"PRAGMA max_page_count={maximumBytes/pageSize}";command.ExecuteScalar();
    }
}
