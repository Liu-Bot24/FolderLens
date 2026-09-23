using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
namespace FolderLens.Infrastructure;

public sealed class BrowsingBudgetException(string? message=null):IOException(message??"浏览数据所在磁盘空间不足，扫描已暂停。已找到的文件仍可浏览；请释放空间后重新打开应用。");
public static class CompactBrowsingCatalog
{
    public const long MaximumBytes=2L<<30;
    private sealed record Budget(long? Pages,long PageSize,string? CatalogDirectory);
    private static readonly ConditionalWeakTable<SqliteConnection,Budget> budgets=new();
    // Keep the indexed sort prefix. SQL still resolves equal values using the
    // original path key; do not duplicate that long key in six B-trees.
    public static void Configure(SqliteConnection connection,long? maximumBytes=MaximumBytes)
    {
        using var command=connection.CreateCommand();command.CommandText="PRAGMA page_size";
        long pageSize=(long)command.ExecuteScalar()!;
        if(maximumBytes is {} limit&&limit<pageSize*256)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using(var transaction=connection.BeginTransaction())
        {
            command.Transaction=transaction;
            foreach(var (name,column) in new[]{("Name","name_sort_key"),("Size","logical_bytes"),("Mtime","mtime_utc_ticks"),("LongEdge","long_edge"),("Pixels","pixel_count"),("Duration","duration_ms")})
            {
                command.CommandText=$"DROP INDEX IF EXISTS IX_Files_{name}; CREATE INDEX IX_Files_{name} ON Files(root_id,entry_state,{column});";command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        if(maximumBytes is {} bounded)
        {command.Transaction=null;command.CommandText=$"PRAGMA max_page_count={bounded/pageSize}";command.ExecuteScalar();}
        string? catalogDirectory=Path.IsPathFullyQualified(connection.DataSource)?Path.GetDirectoryName(connection.DataSource):null;
        budgets.Remove(connection);budgets.Add(connection,new(maximumBytes/pageSize,pageSize,catalogDirectory));
    }
    public static void EnsureWriteHeadroom(SqliteConnection connection,long estimatedBytes=0)
    {
        if(!budgets.TryGetValue(connection,out var budget))return;
        if(budget.Pages is null)
        {
            if(budget.CatalogDirectory is {} directory)
            {
                var drive=new DriveInfo(Path.GetPathRoot(directory)!);
                long freeSpaceReserve=Math.Max(256L<<20,Math.Min(1L<<30,drive.TotalSize/1000));
                if(drive.AvailableFreeSpace<=freeSpaceReserve+estimatedBytes)
                    throw new BrowsingBudgetException("本机浏览数据所在磁盘空间不足，扫描已暂停。已找到的文件仍可浏览；请释放磁盘空间后重启应用。");
            }
            return;
        }
        using var command=connection.CreateCommand();
        command.CommandText="SELECT page_count-freelist_count FROM pragma_page_count,pragma_freelist_count";
        long usedPages=(long)command.ExecuteScalar()!;
        // Leave space for completion state and small user actions. The hard SQLite
        // cap remains the final guard; never grow it or discard observed files.
        long reserve=Math.Min(16L<<20,budget.Pages.Value*budget.PageSize/32);
        if((budget.Pages.Value-usedPages)*budget.PageSize<=reserve+estimatedBytes)throw new BrowsingBudgetException();
    }
}
