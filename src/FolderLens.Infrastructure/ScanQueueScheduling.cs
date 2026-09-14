using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

internal static class ScanQueueScheduling
{
    internal const string NextSql="""
        SELECT directory_id,relative_path,subtree FROM temp.ScanQueue
        WHERE scan_id=$scan AND branch=(SELECT branch FROM temp.ScanBranches
            WHERE scan_id=$scan AND pending>0 ORDER BY last_turn,branch LIMIT 1)
        ORDER BY path_depth,queue_id LIMIT 1
        """;

    internal const string MoveSql="""
        UPDATE temp.ScanQueue
        SET relative_path=$new||substr(relative_path,length($old)+1),
            path_depth=length($new||substr(relative_path,length($old)+1))-length(replace($new||substr(relative_path,length($old)+1),'\',''))+1,
            branch=CASE WHEN instr($new||substr(relative_path,length($old)+1),'\')=0
                THEN $new||substr(relative_path,length($old)+1)
                ELSE substr($new||substr(relative_path,length($old)+1),1,instr($new||substr(relative_path,length($old)+1),'\')-1) END
        WHERE directory_id IN (SELECT directory_id FROM Directories WHERE root_id=$root)
            AND (relative_path=$old OR substr(relative_path,1,length($prefix))=$prefix)
        """;

    internal static void Ensure(SqliteConnection c,SqliteTransaction t)
    {
        // Triggers cover every queue mutation, including exclusions, moves and scan cleanup.
        // Keep exhausted branch history, but exclude it from the runnable index.
        DirectoryIndexer.Execute(c,t,"""
            CREATE TEMP TABLE IF NOT EXISTS ScanQueue(queue_id INTEGER PRIMARY KEY AUTOINCREMENT,scan_id TEXT NOT NULL,directory_id TEXT NOT NULL,relative_path TEXT NOT NULL,subtree INTEGER NOT NULL,path_depth INTEGER NOT NULL,branch TEXT NOT NULL,UNIQUE(scan_id,directory_id));
            CREATE INDEX IF NOT EXISTS temp.IX_ScanQueue_Branch ON ScanQueue(scan_id,branch,path_depth,queue_id);
            CREATE TEMP TABLE IF NOT EXISTS ScanBranches(scan_id TEXT NOT NULL,branch TEXT NOT NULL,last_turn INTEGER NOT NULL DEFAULT 0,pending INTEGER NOT NULL DEFAULT 0 CHECK(pending>=0),PRIMARY KEY(scan_id,branch));
            CREATE INDEX IF NOT EXISTS temp.IX_ScanBranches_Turn ON ScanBranches(scan_id,last_turn,branch);
            CREATE INDEX IF NOT EXISTS temp.IX_ScanBranches_Runnable ON ScanBranches(scan_id,last_turn,branch) WHERE pending>0;
            CREATE TEMP TRIGGER IF NOT EXISTS ScanQueueAdded AFTER INSERT ON ScanQueue BEGIN
                INSERT INTO ScanBranches(scan_id,branch,pending) VALUES(NEW.scan_id,NEW.branch,1)
                    ON CONFLICT(scan_id,branch) DO UPDATE SET pending=pending+1;
            END;
            CREATE TEMP TRIGGER IF NOT EXISTS ScanQueueRemoved AFTER DELETE ON ScanQueue BEGIN
                UPDATE ScanBranches SET pending=pending-1 WHERE scan_id=OLD.scan_id AND branch=OLD.branch;
            END;
            CREATE TEMP TRIGGER IF NOT EXISTS ScanQueueMoved AFTER UPDATE OF branch ON ScanQueue WHEN OLD.branch<>NEW.branch BEGIN
                INSERT INTO ScanBranches(scan_id,branch,pending,last_turn)
                    SELECT NEW.scan_id,NEW.branch,1,last_turn FROM ScanBranches WHERE scan_id=OLD.scan_id AND branch=OLD.branch
                    ON CONFLICT(scan_id,branch) DO UPDATE SET pending=pending+1,last_turn=max(last_turn,excluded.last_turn);
                UPDATE ScanBranches SET pending=pending-1 WHERE scan_id=OLD.scan_id AND branch=OLD.branch;
            END;
            """);
    }
}
