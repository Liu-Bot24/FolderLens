namespace FolderLens.Infrastructure;

public sealed partial class CatalogStore
{
    public Task<long> ReadMediaNavigationTarget(string sessionId,long origin,int steps,CancellationToken cancellation=default)
    {
        if(origin<0)throw new ArgumentOutOfRangeException(nameof(origin));
        if(steps==0)return Task.FromResult(origin);
        return sessionReader.Execute(c=>
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText=$"SELECT ordinal FROM ResultItems WHERE session_id=$s AND ordinal {(steps>0?">":"<")} $origin AND snapshot_kind IN ('image','video') AND EXISTS(SELECT 1 FROM ResultSessions WHERE session_id=$s AND state IN ('building','ready')) ORDER BY ordinal {(steps>0?"ASC":"DESC")} LIMIT $steps";
            cmd.Parameters.AddWithValue("$s",sessionId);cmd.Parameters.AddWithValue("$origin",origin);cmd.Parameters.AddWithValue("$steps",Math.Abs((long)steps));
            long target=origin;using var rows=cmd.ExecuteReader();while(rows.Read()){cancellation.ThrowIfCancellationRequested();target=rows.GetInt64(0);}return target;
        },cancellation);
    }
}
