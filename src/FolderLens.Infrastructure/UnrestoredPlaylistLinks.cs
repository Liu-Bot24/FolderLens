using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record UnrestoredPlaylistLink(long Id,string CollectionId,string Path,string Anchor,string Identity,string Key);

public sealed partial class CatalogStore
{
    // Unproven legacy/unknown mappings remain explicit user data, never silently
    // matched by path. Page them independently of the runtime result snapshot.
    public Task<IReadOnlyList<UnrestoredPlaylistLink>> ReadUnrestoredPlaylistLinks(string collection,long after=0,CancellationToken cancellation=default)=>writer.Execute<IReadOnlyList<UnrestoredPlaylistLink>>(c=>
    {
        if(playlistPath is null)return [];
        using var q=c.CreateCommand();q.CommandText="""
            SELECT p.rowid,p.path,p.anchor,p.directory_identity,p.location_key FROM playlist.SavedLinks p
            WHERE p.collection_id=$collection AND p.rowid>$after AND NOT EXISTS(
                SELECT 1 FROM CollectionMembers m JOIN DirectoryLocations l ON l.location_id=m.directory_location_id
                WHERE m.collection_id=p.collection_id AND m.location_key=p.location_key
                    AND coalesce(l.anchor_locator,'')=p.anchor AND coalesce(l.directory_identity,'')=p.directory_identity AND l.state='active')
            ORDER BY p.rowid LIMIT 64
            """;
        q.Parameters.AddWithValue("$collection",collection);q.Parameters.AddWithValue("$after",after);
        using var rows=q.ExecuteReader();var links=new List<UnrestoredPlaylistLink>();
        while(rows.Read())links.Add(new(rows.GetInt64(0),collection,rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4)));
        return links;
    },cancellation);

    public Task RemoveUnrestoredPlaylistLink(UnrestoredPlaylistLink link,CancellationToken cancellation=default)
        =>RemoveSavedLink(link.CollectionId,link.Id,link.Anchor,link.Identity,link.Key,cancellation);

    // Explicitly authorized by the user: this establishes a NEW favorite at the
    // current path. It is not automatic identity recovery of an unknown old file.
    public async Task RebindUnrestoredPlaylistLink(UnrestoredPlaylistLink link,CancellationToken cancellation=default,string? scanWorkerExecutable=null)
    {
        string path=PathRules.ValidateSource(link.Path),parent=Path.GetDirectoryName(path)!;
        await using var worker=new ScanWorkerClient(scanWorkerExecutable??ScanWorkerClient.FindExecutable()??throw new FileNotFoundException("找不到目录扫描工作进程。"));
        var directory=await worker.Probe(parent,cancellation).ConfigureAwait(false);
        var file=await worker.Probe(path,cancellation).ConfigureAwait(false);
        if(directory.State!="present"||directory.PhysicalIdentity is null||directory.ResolvedLocation is null||directory.CaseMode is not ("sensitive" or "insensitive")||
            file.State!="present"||file.FileObservation is null||file.PhysicalIdentity is null||file.ResolvedLocation!=Path.Combine(directory.ResolvedLocation,Path.GetFileName(path)))
            throw new IOException("当前无法确认这个位置的文件，原收藏记录已保留。");
        string key=FileLocationKey(parent,Path.GetFileName(path),directory.CaseMode,directory.PhysicalIdentity,file.PhysicalIdentity,"");
        await writer.Execute(c=>
        {
            using var t=c.BeginTransaction();
            int removed=DirectoryIndexer.Execute(c,t,"DELETE FROM playlist.SavedLinks WHERE rowid=$row AND collection_id=$id AND path=$path AND anchor=$anchor AND directory_identity=$identity AND location_key=$key",
                ("$row",link.Id),("$id",link.CollectionId),("$path",link.Path),("$anchor",link.Anchor),("$identity",link.Identity),("$key",link.Key));
            if(removed!=1)throw new IOException("收藏记录已变化，请重新检查。");
            DirectoryIndexer.Execute(c,t,"INSERT OR IGNORE INTO playlist.SavedLinks(collection_id,anchor,directory_identity,location_key,path,added_utc_ticks,file_identity,case_mode) VALUES($id,$anchor,$identity,$key,$path,$time,$file,$case)",
                ("$id",link.CollectionId),("$anchor",directory.ResolvedLocation),("$identity",directory.PhysicalIdentity),("$key",key),("$path",path),("$time",DateTime.UtcNow.Ticks),("$file",file.PhysicalIdentity),("$case",directory.CaseMode));
            t.Commit();return true;
        },cancellation).ConfigureAwait(false);
        await ObservePlaylistFile(parent,Path.GetFileName(path),directory,file,cancellation).ConfigureAwait(false);
    }
}
