using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record CapacityReport(string ScopeId,string State,IReadOnlyList<CapacityRow> Rows)
{
    public DateTimeOffset CalculatedAt {get;init;}=DateTimeOffset.Now;
    public long Pending {get;init;}
    public long Unresolvable {get;init;}
    public long Revision {get;init;}
    public bool IsComplete=>State is "ready" or "snapshot";
}
public sealed class CapacityService(CatalogStore catalog)
{
    public Task<(string State,long Revision)> ChangeStamp(string root,CancellationToken cancellation)=>catalog.Read(c=>
    {
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT scan_state,catalog_revision FROM Roots CROSS JOIN SchemaInfo WHERE root_id=$root";cmd.Parameters.AddWithValue("$root",root);
        using var row=cmd.ExecuteReader();if(!row.Read())throw new InvalidOperationException("目录索引已不存在。");return(row.GetString(0),row.GetInt64(1));
    },cancellation);
    public Task<CapacityReport> EntireRoot(string root,CancellationToken cancellation,long? observedRootEpoch=null)=>catalog.Read(c=>
    {
        using var transaction=c.BeginTransaction(deferred:true);using var state=c.CreateCommand();state.Transaction=transaction;state.CommandText="SELECT scan_state,catalog_revision FROM Roots CROSS JOIN SchemaInfo WHERE root_id=$root";state.Parameters.AddWithValue("$root",root);
        string status;long revision;using(var row=state.ExecuteReader()){if(!row.Read())throw new InvalidOperationException("该根目录没有可用的索引统计。");status=row.GetString(0);revision=row.GetInt64(1);}
        IEnumerable<DirectoryCapacity> Directories()
        {
            using var cmd=c.CreateCommand();cmd.Transaction=transaction;
            // Use the existing directory index. Transfer one aggregate per directory, not one path/object per file.
            cmd.CommandText="""
                SELECT d.relative_path,COUNT(f.entry_id),COALESCE(SUM(f.logical_bytes),0),
                    COALESCE(SUM(f.allocated_bytes),0),COUNT(f.entry_id)-COUNT(f.allocated_bytes)
                FROM Directories d LEFT JOIN Files f INDEXED BY IX_Files_Directory
                    ON f.root_id=d.root_id AND f.directory_id=d.directory_id AND f.entry_state='present'
                        AND ($epoch IS NULL OR f.last_seen_scan_id IN(SELECT scan_id FROM ScanRuns WHERE root_id=$root AND root_epoch=$epoch))
                WHERE d.root_id=$root AND (d.entry_state='present' OR f.entry_id IS NOT NULL)
                GROUP BY d.directory_id
                """;
            cmd.Parameters.AddWithValue("$root",root);cmd.Parameters.AddWithValue("$epoch",(object?)observedRootEpoch??DBNull.Value);using var rows=cmd.ExecuteReader();
            while(rows.Read()){cancellation.ThrowIfCancellationRequested();yield return new(rows.GetString(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3),rows.GetInt64(4));}
        }
        var result=Capacity.BuildDirectories(Directories(),cancellation);transaction.Commit();return new CapacityReport(root,status,result){Revision=revision};
    },cancellation);
    public async Task<CapacityReport> Result(ResultHandle handle,CancellationToken cancellation)
    {
        if(handle.IsPendingView)throw new InvalidOperationException("待判断视图不能作为已匹配文件容量统计。");
        // Feed a bounded producer to a dedicated aggregate task; do not materialize all result rows.
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);var token=stop.Token;
        var channel=System.Threading.Channels.Channel.CreateBounded<CapacityFile>(512);
        var aggregate=Task.Run(()=>Capacity.Build(Read(),cancellation:token),CancellationToken.None);
        IEnumerable<CapacityFile> Read(){while(channel.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())while(channel.Reader.TryRead(out var row))yield return row;}
        async Task Produce()
        {
            Exception? failure=null;
            try{for(long offset=0;offset<handle.Count;offset+=256)foreach(var row in await catalog.ReadPage(handle.Id,offset,256,token))await channel.Writer.WriteAsync(new(row.RelativePath,row.Bytes,row.Allocated),token);}
            catch(Exception error){failure=error;throw;}
            finally{channel.Writer.TryComplete(failure);}
        }
        var producer=Produce();var first=await Task.WhenAny(aggregate,producer);
        if(!first.IsCompletedSuccessfully)stop.Cancel();
        await Task.WhenAll(aggregate,producer);
        return new(handle.Id,"snapshot",await aggregate){Pending=handle.Pending,Unresolvable=handle.Unresolvable};
    }
}
