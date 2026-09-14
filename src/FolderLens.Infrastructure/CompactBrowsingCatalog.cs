using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
namespace FolderLens.Infrastructure;

public sealed class BrowsingBudgetException():IOException("已达到本次浏览的存储上限，扫描已停止。已找到的文件仍可浏览；请关闭后重新打开应用，并选择较小的文件夹。");
public static class CompactBrowsingCatalog
{
    public const long MaximumBytes=2L<<30;
    private sealed record Budget(long Pages,long PageSize);
    private static readonly ConditionalWeakTable<SqliteConnection,Budget> budgets=new();
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
        budgets.Remove(connection);budgets.Add(connection,new(maximumBytes/pageSize,pageSize));
    }
    public static void EnsureWriteHeadroom(SqliteConnection connection,long estimatedBytes=0)
    {
        if(!budgets.TryGetValue(connection,out var budget))return;
        using var command=connection.CreateCommand();
        command.CommandText="SELECT page_count-freelist_count FROM pragma_page_count,pragma_freelist_count";
        long usedPages=(long)command.ExecuteScalar()!;
        // Leave space for completion state and small user actions. The hard SQLite
        // cap remains the final guard; never grow it or discard observed files.
        long reserve=Math.Min(16L<<20,budget.Pages*budget.PageSize/32);
        if((budget.Pages-usedPages)*budget.PageSize<=reserve+estimatedBytes)throw new BrowsingBudgetException();
    }
}
