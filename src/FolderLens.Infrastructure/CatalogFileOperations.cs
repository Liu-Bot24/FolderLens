using FolderLens.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FolderLens.Infrastructure;

public sealed record OperatedLink(string Collection,string Anchor,string DirectoryIdentity,string Key,long Added,string? FileIdentity);
public sealed record FileOperationTarget(SnapshotItem Item,string Path,SourceFileStamp Stamp,string PhysicalIdentity,string LocationKey,IReadOnlyList<OperatedLink> Links);

public sealed partial class CatalogStore
{
    // Unlike external launch, mutation never follows a newer path or uses a format whitelist.
    public Task<FileOperationTarget> ResolveFileOperation(SnapshotItem item,CancellationToken token=default)=>writer.Execute(c=>
    {
        if(File.Exists(CompletionJournal))throw new IOException("上次文件操作的收藏清理尚未完成，请重新打开应用后重试。");
        using var transaction=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT f.file_version,f.path_revision,f.relative_path,f.directory_id,f.root_id,r.display_path,r.root_epoch,b.location_id,b.binding_revision,f.physical_identity,f.logical_bytes,f.mtime_utc_ticks,f.location_key,f.entry_state,f.hydration_state,l.state FROM Files f JOIN Roots r ON r.root_id=f.root_id JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id WHERE f.entry_id=$id";
        command.Parameters.AddWithValue("$id",item.EntryId);
        string path,physical,key;SourceFileStamp stamp;
        using(var row=command.ExecuteReader())
        {
            if(!row.Read()||row.GetInt64(0)!=item.Version||row.GetInt64(1)!=item.PathRevision||row.GetString(2)!=item.RelativePath||row.GetString(3)!=item.DirectoryId||row.GetString(4)!=item.SourceRootId||row.GetString(5)!=item.SourceRootPath||row.GetInt64(6)!=item.SourceRootEpoch||row.GetString(7)!=item.DirectoryLocationId||row.GetInt64(8)!=item.BindingRevision||row.IsDBNull(9)||row.GetString(9)!=item.PhysicalIdentity||row.GetString(13)!="present"||row.GetString(14)!="local"||row.GetString(15)!="active")
                throw new IOException("所选文件或位置已变化，请刷新后重新选择。");
            string basePath=Path.GetFullPath(row.GetString(5)).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
            path=PathRules.ValidateSource(Path.GetFullPath(Path.Combine(basePath,row.GetString(2))));
            if(!path.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))throw new IOException("文件路径已超出当前目录。");
            physical=row.GetString(9);stamp=new(row.GetInt64(10),row.GetInt64(11));key=row.GetString(12);
        }
        var links=new List<OperatedLink>();
        if(playlistPath is not null)
        {
            command.CommandText="SELECT p.collection_id,p.anchor,p.directory_identity,p.location_key,p.added_utc_ticks,p.file_identity FROM playlist.SavedLinks p JOIN DirectoryLocations l ON p.anchor=coalesce(l.anchor_locator,'') AND p.directory_identity=coalesce(l.directory_identity,'') WHERE l.location_id=$location AND p.location_key=$key";
            command.Parameters.AddWithValue("$location",item.DirectoryLocationId!);command.Parameters.AddWithValue("$key",key);
            using var rows=command.ExecuteReader();while(rows.Read())links.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetInt64(4),rows.IsDBNull(5)?null:rows.GetString(5)));
        }
        transaction.Commit();return new FileOperationTarget(item,path,stamp,physical,key,links);
    },token);

    private string CompletionJournal=> (playlistPath??catalogPath)+".completed-operation.json";
    // The caller keeps its shutdown registration until this non-cancellable completion ends.
    // A tiny journal contains only the completed operation, never a directory/index copy.
    public async Task CompleteFileOperation(FileOperationTarget target)
    {
        var settings=new AtomicSettings(Path.GetDirectoryName(CompletionJournal)!);
        try{await settings.Save(Path.GetFileName(CompletionJournal),target);}
        catch(Exception journalError)
        {
            // Even a full/read-only journal volume must not skip required cleanup.
            try{await RetireFileOperation(target);return;}
            catch(Exception cleanupError){throw new IOException("文件操作已完成，但收藏清理及恢复记录均无法保存，请检查收藏夹。",new AggregateException(journalError,cleanupError));}
        }
        await RetireFileOperation(target);
        File.Delete(CompletionJournal);
    }
    private async Task RecoverCompletedFileOperation()
    {
        var settings=new AtomicSettings(Path.GetDirectoryName(CompletionJournal)!);
        if(await settings.Load<FileOperationTarget>(Path.GetFileName(CompletionJournal)) is {} target)
        {await RetireFileOperation(target);File.Delete(CompletionJournal);}
    }
    private Task RetireFileOperation(FileOperationTarget target)=>writer.Execute(c=>
    {
        using var transaction=c.BeginTransaction();
        if(playlistPath is not null)foreach(var link in target.Links)
            DirectoryIndexer.Execute(c,transaction,"DELETE FROM playlist.SavedLinks WHERE collection_id=$collection AND anchor=$anchor AND directory_identity=$directory AND location_key=$key AND added_utc_ticks=$added AND file_identity IS $physical",("$collection",link.Collection),("$anchor",link.Anchor),("$directory",link.DirectoryIdentity),("$key",link.Key),("$added",link.Added),("$physical",(object?)link.FileIdentity??DBNull.Value));
        // Cleanup does not depend on the old row still existing: a watcher may already
        // have retired it. Never mark a replacement at that path missing.
        int retired=DirectoryIndexer.Execute(c,transaction,"UPDATE Files SET entry_state='missing',file_version=file_version+1,path_revision=path_revision+1 WHERE entry_id=$id AND relative_path=$path AND root_id=$root AND physical_identity=$physical AND entry_state<>'missing'",("$id",target.Item.EntryId),("$path",target.Item.RelativePath),("$root",target.Item.SourceRootId!),("$physical",target.PhysicalIdentity));
        if(retired==0)
        {
            // Remove only captured memberships; a new mapping added later survives.
            foreach(var link in target.Links)DirectoryIndexer.Execute(c,transaction,"DELETE FROM CollectionMembers WHERE collection_id=$collection AND directory_location_id=$location AND location_key=$key AND added_utc_ticks=$added",("$collection",link.Collection),("$location",target.Item.DirectoryLocationId!),("$key",target.LocationKey),("$added",link.Added));
        }
        transaction.Commit();return retired;
    },CancellationToken.None);

    // Compatibility for non-durable catalog tests; production uses captured completion.
    public Task RetireOperatedFile(SnapshotItem item,CancellationToken token=default)=>writer.Execute(c=>
    {
        using var command=c.CreateCommand();
        command.CommandText="UPDATE Files SET entry_state='missing',file_version=file_version+1,path_revision=path_revision+1 WHERE entry_id=$id AND file_version=$version AND relative_path=$path";
        command.Parameters.AddWithValue("$id",item.EntryId);command.Parameters.AddWithValue("$version",item.Version);command.Parameters.AddWithValue("$path",item.RelativePath);
        return command.ExecuteNonQuery();
    },token);
}
