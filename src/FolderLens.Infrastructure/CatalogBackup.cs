using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FolderLens.Infrastructure;

internal static class CatalogBackup
{
    internal static void Copy(SqliteConnection source,SqliteConnection destination,CancellationToken cancellation,Action<int,int>? progress=null)
    {
        cancellation.ThrowIfCancellationRequested();
        using var backup=raw.sqlite3_backup_init(destination.Handle,"main",source.Handle,"main");
        if(backup is null)throw new SqliteException("无法创建索引备份。",raw.sqlite3_errcode(destination.Handle));
        var waiting=Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            int result=raw.sqlite3_backup_step(backup,256);
            progress?.Invoke(raw.sqlite3_backup_pagecount(backup)-raw.sqlite3_backup_remaining(backup),raw.sqlite3_backup_pagecount(backup));
            if(result==raw.SQLITE_DONE)break;
            if(result==raw.SQLITE_BUSY||result==raw.SQLITE_LOCKED)
            {if(waiting.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("索引备份等待数据库释放超时。");cancellation.WaitHandle.WaitOne(20);}
            else if(result!=raw.SQLITE_OK)throw new SqliteException("索引备份失败。",result);
            else waiting.Restart();
        }
        cancellation.ThrowIfCancellationRequested();
        int finished=raw.sqlite3_backup_finish(backup);
        if(finished!=raw.SQLITE_OK)throw new SqliteException("索引备份未完成。",finished);
    }
}
