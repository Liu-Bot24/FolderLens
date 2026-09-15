using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed partial class CatalogStore
{
    // Called only after a successful explicit filesystem operation. The existing
    // invalidation trigger also removes old collection mappings across aliases.
    public Task RetireOperatedFile(SnapshotItem item,CancellationToken token=default)=>writer.Execute(c=>
    {
        using var command=c.CreateCommand();
        command.CommandText="UPDATE Files SET entry_state='missing',file_version=file_version+1,path_revision=path_revision+1 WHERE entry_id=$id AND file_version=$version AND relative_path=$path";
        command.Parameters.AddWithValue("$id",item.EntryId);command.Parameters.AddWithValue("$version",item.Version);command.Parameters.AddWithValue("$path",item.RelativePath);
        return command.ExecuteNonQuery();
    },token);
}
