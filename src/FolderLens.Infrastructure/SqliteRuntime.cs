using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

/// <summary>Process-wide SQLite settings; they only apply before the first connection opens.</summary>
internal static class SqliteRuntime
{
    private const int ConfigStatementJournalSpill=26;
    // Catalog upserts fire triggers and foreign-key checks, so each one runs in a
    // statement journal of roughly 16 pages. With temp_store=FILE, SQLite's 64 KiB
    // default spilled that journal to a temp file for the rest of almost every scan
    // batch, writing about four times the WAL volume. Larger statements still spill.
    internal const int StatementJournalSpillBytes=256<<10;
    internal static bool StatementJournalConfigured {get;}=Configure();
    internal static void EnsureConfigured()=>_=StatementJournalConfigured;
    private static bool Configure()
    {
        // Microsoft.Data.Sqlite selects the native provider in its static constructor.
        RuntimeHelpers.RunClassConstructor(typeof(SqliteConnection).TypeHandle);
        // SQLITE_MISUSE means SQLite was already initialized in this process; the
        // default threshold then remains, which is slower but equally correct.
        return SQLitePCL.raw.sqlite3_config(ConfigStatementJournalSpill,StatementJournalSpillBytes)==SQLitePCL.raw.SQLITE_OK;
    }
}
