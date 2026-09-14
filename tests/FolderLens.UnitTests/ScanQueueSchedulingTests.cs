using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScanQueueSchedulingTests
{
    [Fact]
    public void MovingQueuedSubtreeUpdatesDepthBranchCountsAndFairTurn()
    {
        using var c=Open();using var t=c.BeginTransaction();
        Exec(c,t,"CREATE TABLE Directories(directory_id TEXT PRIMARY KEY,root_id TEXT);INSERT INTO Directories VALUES('a0','root'),('a1','root'),('a2','root'),('b','root')");
        for(int i=0;i<3;i++)Queue(c,t,"s","a"+i,@"A\child"+i);
        Queue(c,t,"s","b",@"B\small");
        Exec(c,t,"UPDATE ScanBranches SET last_turn=10 WHERE branch='A'");
        DirectoryIndexer.Execute(c,t,ScanQueueScheduling.MoveSql,("$root","root"),("$old","A"),("$new",@"M\A"),("$prefix",@"A\"));
        Assert.Equal("b",Next(c,t));
        Assert.Equal(3,Scalar(c,t,"SELECT count(*) FROM ScanQueue WHERE branch='M' AND path_depth=3 AND relative_path LIKE 'M%'") );
        Assert.Equal(0,Scalar(c,t,"SELECT pending FROM ScanBranches WHERE branch='A'"));
        Assert.Equal(3,Scalar(c,t,"SELECT pending FROM ScanBranches WHERE branch='M'"));
        Assert.Equal(10,Scalar(c,t,"SELECT last_turn FROM ScanBranches WHERE branch='M'"));
        Assert.Equal(4,Scalar(c,t,"SELECT count(*) FROM ScanQueue"));
    }

    [Fact]
    public void ExhaustedBranchesLeaveRunnableIndexButKeepHistoryAndRequeueCounts()
    {
        using var c=Open();using var t=c.BeginTransaction();
        Queue(c,t,"s","live",@"live\child");
        Exec(c,t,"UPDATE ScanBranches SET last_turn=20 WHERE branch='live'");
        string? before=Next(c,t);
        Exec(c,t,"WITH RECURSIVE n(i) AS(SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<5000) INSERT INTO ScanBranches(scan_id,branch,last_turn,pending) SELECT 's','dead'||i,1,0 FROM n");
        Assert.Equal(before,Next(c,t));
        using(var cmd=c.CreateCommand())
        {
            cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+ScanQueueScheduling.NextSql;cmd.Parameters.AddWithValue("$scan","s");
            using var rows=cmd.ExecuteReader();var plan=new List<string>();while(rows.Read())plan.Add(rows.GetString(3));
            Assert.Contains(plan,line=>line.Contains("IX_ScanBranches_Runnable",StringComparison.Ordinal));
        }
        Queue(c,t,"s","live",@"live\child"); // ignored duplicate does not inflate count
        Assert.Equal(1,Scalar(c,t,"SELECT pending FROM ScanBranches WHERE branch='live'"));
        Exec(c,t,"DELETE FROM ScanQueue WHERE scan_id='s'");Assert.Null(Next(c,t));
        Queue(c,t,"s","new",@"live\new");
        Assert.Equal(20,Scalar(c,t,"SELECT last_turn FROM ScanBranches WHERE branch='live'"));
        Assert.Equal(1,Scalar(c,t,"SELECT pending FROM ScanBranches WHERE branch='live'"));
        Queue(c,t,"other","other",@"live\other");
        Exec(c,t,"DELETE FROM ScanQueue WHERE scan_id='s'");
        Assert.Equal(1,Scalar(c,t,"SELECT pending FROM ScanBranches WHERE scan_id='other'"));
    }

    private static SqliteConnection Open()
    {
        var c=new SqliteConnection("Data Source=:memory:");c.Open();using var t=c.BeginTransaction();ScanQueueScheduling.Ensure(c,t);t.Commit();return c;
    }
    private static void Queue(SqliteConnection c,SqliteTransaction t,string scan,string id,string path)=>DirectoryIndexer.Execute(c,t,
        "INSERT OR IGNORE INTO ScanQueue(scan_id,directory_id,relative_path,subtree,path_depth,branch) VALUES($s,$id,$p,1,$d,$b)",
        ("$s",scan),("$id",id),("$p",path),("$d",path.Count(ch=>ch=='\\')+1),("$b",ScanPriority.Branch(path)));
    private static void Exec(SqliteConnection c,SqliteTransaction t,string sql)=>DirectoryIndexer.Execute(c,t,sql);
    private static long Scalar(SqliteConnection c,SqliteTransaction t,string sql){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;return (long)cmd.ExecuteScalar()!;}
    private static string? Next(SqliteConnection c,SqliteTransaction t){using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=ScanQueueScheduling.NextSql;cmd.Parameters.AddWithValue("$scan","s");return cmd.ExecuteScalar() as string;}
}
