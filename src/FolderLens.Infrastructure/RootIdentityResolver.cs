using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record OpenedRoot(string RootId,long Epoch,string Availability,bool IdentityChanged,bool HasPreviousIndex);

/// <summary>Preserves a separate indexed root for each observed volume/directory identity at the same display path.</summary>
public sealed class RootIdentityResolver(CatalogStore catalog,string? scanWorkerExecutable=null,TimeSpan? operationTimeout=null)
{
    public async Task<OpenedRoot> Open(string path,CancellationToken cancellation=default,(string RootId,long Epoch)? ongoingScan=null)
    {
        path=PathRules.ValidateSource(path);
        var previous=await catalog.Read(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT root_id,volume_identity,cloud_policy FROM Roots WHERE display_path=$path ORDER BY last_checked_utc_ticks DESC,root_epoch DESC LIMIT 1";cmd.Parameters.AddWithValue("$path",path);
            using var row=cmd.ExecuteReader();return row.Read()?(Id:row.GetString(0),Identity:row.IsDBNull(1)?null:row.GetString(1),Cloud:row.GetString(2)):(Id:(string?)null,Identity:(string?)null,Cloud:"localOnly");
        },cancellation).ConfigureAwait(false);
        string? physical=null;string availability="unknown";
        string? executable=scanWorkerExecutable??ScanWorkerClient.FindExecutable();
        if(executable is null)throw new FileNotFoundException("找不到目录扫描工作进程，无法安全核验根目录身份。");
        await using(var worker=new ScanWorkerClient(executable,operationTimeout))
        {
            try
            {
                await foreach(var packet in worker.Read(path,previous.Cloud=="explicitAllowed",cancellation).ConfigureAwait(false))
                {
                    if(packet.State=="started"){physical=packet.PhysicalIdentity;availability="online";break;}
                    if(packet.State is "offline" or "inaccessible"){availability=packet.State;break;}
                    if(packet.State is "failed" or "excluded"){availability="unknown";break;}
                }
            }
            catch(ScanWorkerUnavailableException){throw;}
            catch(TimeoutException){availability="offline";}
            catch(IOException){availability="offline";}
        }
        // No source identity could be observed while offline. Reopen the last known index, never invent a volume ID.
        return await catalog.Write(c=>
        {
            using var transaction=c.BeginTransaction();ScanRecovery.Recover(c,transaction);string? id=null;bool changed=false,hasIndex=false;
            if(physical is not null)
            {
                using var find=c.CreateCommand();find.Transaction=transaction;find.CommandText="SELECT root_id FROM Roots WHERE display_path=$path AND volume_identity=$identity LIMIT 1";find.Parameters.AddWithValue("$path",path);find.Parameters.AddWithValue("$identity",physical);id=find.ExecuteScalar() as string;
                hasIndex=id is not null;
                if(id is null && previous.Id is not null && previous.Identity is null){id=previous.Id;hasIndex=true;}
                changed=previous.Identity is not null && previous.Identity!=physical;
                id??=DirectoryIndexer.StablePathId("rootIdentity",path+"\0"+physical);
            }
            else{id=previous.Id;hasIndex=id is not null;id??=DirectoryIndexer.StablePathId("root",path);}
            using var open=c.CreateCommand();open.Transaction=transaction;open.CommandText="""
            INSERT INTO Roots(root_id,display_path,canonical_key,volume_identity,root_epoch,availability,scan_state,last_checked_utc_ticks)
            VALUES($id,$path,$key,$identity,1,$availability,'notStarted',$now)
            ON CONFLICT(root_id) DO UPDATE SET root_epoch=CASE WHEN Roots.root_id=$ongoing AND Roots.root_epoch=$epoch THEN Roots.root_epoch ELSE Roots.root_epoch+1 END,availability=excluded.availability,
                volume_identity=COALESCE(excluded.volume_identity,Roots.volume_identity),last_checked_utc_ticks=excluded.last_checked_utc_ticks
            RETURNING root_epoch;
            """;
            open.Parameters.AddWithValue("$id",id);open.Parameters.AddWithValue("$path",path);open.Parameters.AddWithValue("$key",physical is null?path:path+"\0"+physical);
            open.Parameters.AddWithValue("$identity",(object?)physical??DBNull.Value);open.Parameters.AddWithValue("$availability",availability);open.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
            open.Parameters.AddWithValue("$ongoing",ongoingScan?.RootId??"");open.Parameters.AddWithValue("$epoch",ongoingScan?.Epoch??-1);
            long epoch=(long)open.ExecuteScalar()!;transaction.Commit();return new OpenedRoot(id,epoch,availability,changed,hasIndex);
        },cancellation).ConfigureAwait(false);
    }
}
