using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

/// <summary>Only proven-dead process owners may turn a persisted running scan into an abandoned scan.</summary>
internal static class ScanRecovery
{
    public static void Ensure(SqliteConnection connection,SqliteTransaction transaction)
    {
        DirectoryIndexer.Execute(connection,transaction,"CREATE TABLE IF NOT EXISTS ScanOwners(scan_id TEXT PRIMARY KEY REFERENCES ScanRuns(scan_id) ON DELETE CASCADE,process_id INTEGER NOT NULL,process_started_ticks INTEGER NOT NULL)");
    }
    public static void Recover(SqliteConnection connection,SqliteTransaction transaction)
    {
        Ensure(connection,transaction);var abandoned=new List<string>();
        using(var command=connection.CreateCommand())
        {
            command.Transaction=transaction;command.CommandText="SELECT s.scan_id,o.process_id,o.process_started_ticks FROM ScanRuns s LEFT JOIN ScanOwners o ON o.scan_id=s.scan_id WHERE s.state='running'";
            using var rows=command.ExecuteReader();
            while(rows.Read())
            {
                if(rows.IsDBNull(1)){abandoned.Add(rows.GetString(0));continue;}
                try
                {
                    using var owner=Process.GetProcessById(rows.GetInt32(1));
                    if(owner.HasExited||owner.StartTime.ToUniversalTime().Ticks!=rows.GetInt64(2))abandoned.Add(rows.GetString(0));
                }
                catch(ArgumentException){abandoned.Add(rows.GetString(0));}
                catch(InvalidOperationException){abandoned.Add(rows.GetString(0));}
                catch(Win32Exception){/* Owner liveness is unknown: preserve running state rather than invent a crash. */}
            }
        }
        foreach(string scan in abandoned)
        {
            DirectoryIndexer.Execute(connection,transaction,"UPDATE DirectoryScans SET state='failed',error_code='PreviousProcessExited' WHERE scan_id=$scan AND state IN ('queued','enumerating')",("$scan",scan));
            DirectoryIndexer.Execute(connection,transaction,"UPDATE ScanRuns SET state='failed',completed_utc_ticks=$now WHERE scan_id=$scan",("$scan",scan),("$now",DateTime.UtcNow.Ticks));
            DirectoryIndexer.Execute(connection,transaction,"UPDATE Roots SET scan_state='partial' WHERE root_id=(SELECT root_id FROM ScanRuns WHERE scan_id=$scan) AND scan_state='scanning' AND NOT EXISTS(SELECT 1 FROM ScanRuns active WHERE active.root_id=Roots.root_id AND active.state='running')",("$scan",scan));
            DirectoryIndexer.Execute(connection,transaction,"DELETE FROM ScanOwners WHERE scan_id=$scan",("$scan",scan));
        }
    }
    public static void Own(SqliteConnection connection,SqliteTransaction transaction,string scan)
    {
        Ensure(connection,transaction);using var process=Process.GetCurrentProcess();
        DirectoryIndexer.Execute(connection,transaction,"INSERT INTO ScanOwners(scan_id,process_id,process_started_ticks) VALUES($scan,$pid,$start)",("$scan",scan),("$pid",process.Id),("$start",process.StartTime.ToUniversalTime().Ticks));
    }
}
