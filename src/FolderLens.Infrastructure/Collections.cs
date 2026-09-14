using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed record FileCollection(string Id,string Name,long Count);

public sealed partial class CatalogStore
{
    // This connection owns catalog writes. A transactional TEMP counter tracks
    // actual member changes without a persistent schema change or scan polling IO.
    private static int InitializeCollectionRevision(SqliteConnection c)=>Execute(c,"""
        CREATE TEMP TABLE IF NOT EXISTS CollectionRevision(value INTEGER NOT NULL);
        INSERT INTO temp.CollectionRevision SELECT 0 WHERE NOT EXISTS(SELECT 1 FROM temp.CollectionRevision);
        CREATE TEMP TRIGGER IF NOT EXISTS CollectionRevisionInsert AFTER INSERT ON main.CollectionMembers BEGIN UPDATE CollectionRevision SET value=value+1; END;
        CREATE TEMP TRIGGER IF NOT EXISTS CollectionRevisionDelete AFTER DELETE ON main.CollectionMembers BEGIN UPDATE CollectionRevision SET value=value+1; END;
        CREATE TEMP TRIGGER IF NOT EXISTS CollectionRevisionUpdate AFTER UPDATE ON main.CollectionMembers
        WHEN OLD.collection_id IS NOT NEW.collection_id OR OLD.location_key IS NOT NEW.location_key OR OLD.entry_id IS NOT NEW.entry_id OR OLD.directory_location_id IS NOT NEW.directory_location_id
        BEGIN UPDATE CollectionRevision SET value=value+1; END;
        """);
    public Task<long> ReadCollectionRevision(CancellationToken cancellation=default)=>writer.Execute(c=>Scalar(c,"SELECT value FROM temp.CollectionRevision"),cancellation);
    public Task<bool[]> ReadCollectionFlags(IReadOnlyList<SnapshotItem> items,CancellationToken cancellation=default)=>interactiveReader.Execute(c=>
    {
        if(items.Count>256)throw new ArgumentException("收藏状态批次过大。");
        using var command=c.CreateCommand();command.CommandText="""
            SELECT EXISTS(SELECT 1 FROM Files f JOIN Roots r ON r.root_id=f.root_id JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id
                WHERE f.entry_id=$entry AND f.file_version=$version AND f.path_revision=$revision AND f.relative_path=$path
                AND f.root_id=$root AND r.root_epoch=$epoch AND f.entry_state<>'missing' AND l.state='active' AND b.location_id=$location AND b.binding_revision=$binding
                AND EXISTS(SELECT 1 FROM CollectionMembers m WHERE m.location_key=f.location_key AND m.directory_location_id=(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=f.directory_id)))
            """;
        foreach(string parameter in new[]{"$entry","$version","$revision","$path","$root","$epoch","$location","$binding"})command.Parameters.AddWithValue(parameter,DBNull.Value);
        var flags=new bool[items.Count];for(int i=0;i<items.Count;i++)
        {
            cancellation.ThrowIfCancellationRequested();var item=items[i];
            command.Parameters["$entry"].Value=item.EntryId;command.Parameters["$version"].Value=item.Version;command.Parameters["$revision"].Value=item.PathRevision;
            command.Parameters["$path"].Value=item.RelativePath;command.Parameters["$root"].Value=(object?)item.SourceRootId??DBNull.Value;command.Parameters["$epoch"].Value=(object?)item.SourceRootEpoch??DBNull.Value;
            command.Parameters["$location"].Value=(object?)item.DirectoryLocationId??DBNull.Value;command.Parameters["$binding"].Value=item.BindingRevision;flags[i]=(long)command.ExecuteScalar()! == 1;
        }
        return flags;
    },cancellation);
    internal static string LocationKey(string root,string relative,string caseMode)
    {
        string path=Path.Combine(root,relative).Replace('/','\\');
        return caseMode=="sensitive"?path:path.ToUpperInvariant();
    }
    private static void MigrateCollections(SqliteConnection c,string name,bool existing)
    {
        using var transaction=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=transaction;
        command.CommandText=name=="catalog"?"""
            ALTER TABLE Files ADD COLUMN location_key TEXT NOT NULL DEFAULT '';
            UPDATE Files SET location_key=(SELECT lens_location(r.display_path,Files.relative_path,d.case_mode) FROM Roots r JOIN Directories d ON d.directory_id=Files.directory_id WHERE r.root_id=Files.root_id);
            CREATE INDEX IX_Files_Location ON Files(location_key,entry_state,entry_id);
            CREATE TRIGGER Files_Location_Insert AFTER INSERT ON Files BEGIN
                UPDATE Files SET location_key=(SELECT lens_location(r.display_path,NEW.relative_path,d.case_mode) FROM Roots r JOIN Directories d ON d.directory_id=NEW.directory_id WHERE r.root_id=NEW.root_id) WHERE entry_id=NEW.entry_id;
            END;
            CREATE TRIGGER Files_Location_Update AFTER UPDATE OF relative_path,root_id,directory_id ON Files BEGIN
                UPDATE Files SET location_key=(SELECT lens_location(r.display_path,NEW.relative_path,d.case_mode) FROM Roots r JOIN Directories d ON d.directory_id=NEW.directory_id WHERE r.root_id=NEW.root_id) WHERE entry_id=NEW.entry_id;
            END;
            CREATE TRIGGER Roots_Location_Update AFTER UPDATE OF display_path ON Roots WHEN OLD.display_path<>NEW.display_path BEGIN
                UPDATE Files SET location_key=lens_location(NEW.display_path,relative_path,(SELECT case_mode FROM Directories WHERE directory_id=Files.directory_id)) WHERE root_id=NEW.root_id;
            END;
            CREATE TRIGGER Directories_Location_Update AFTER UPDATE OF case_mode ON Directories WHEN OLD.case_mode<>NEW.case_mode BEGIN
                UPDATE Files SET location_key=lens_location((SELECT display_path FROM Roots WHERE root_id=Files.root_id),relative_path,NEW.case_mode) WHERE root_id=NEW.root_id AND directory_id=NEW.directory_id;
            END;
            CREATE TABLE Collections(collection_id TEXT PRIMARY KEY,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,created_utc_ticks INTEGER NOT NULL) STRICT;
            CREATE TABLE CollectionMembers(collection_id TEXT NOT NULL REFERENCES Collections(collection_id) ON DELETE CASCADE,location_key TEXT NOT NULL,entry_id TEXT NOT NULL,added_utc_ticks INTEGER NOT NULL,PRIMARY KEY(collection_id,location_key)) STRICT;
            CREATE INDEX IX_CollectionMembers_Location ON CollectionMembers(location_key,collection_id);
            CREATE TRIGGER CollectionMembers_FollowKnownRename AFTER UPDATE OF location_key ON Files WHEN OLD.location_key<>NEW.location_key BEGIN
                DELETE FROM CollectionMembers WHERE location_key=OLD.location_key AND collection_id IN(SELECT collection_id FROM CollectionMembers WHERE location_key=NEW.location_key);
                UPDATE CollectionMembers SET location_key=NEW.location_key,entry_id=NEW.entry_id WHERE location_key=OLD.location_key;
            END;
            UPDATE SchemaInfo SET schema_version=4;
            PRAGMA user_version=4;
            """:"""
            ALTER TABLE ResultItems ADD COLUMN source_root_id TEXT;
            ALTER TABLE ResultItems ADD COLUMN source_root_path TEXT;
            ALTER TABLE ResultItems ADD COLUMN source_root_epoch INTEGER;
            PRAGMA user_version=4;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
    private static string CollectionName(string name)
    {
        name=name.Trim();if(name.Length is <1 or >100||name.Any(char.IsControl))throw new ArgumentException("收藏夹名称须为 1–100 个字符。");return name;
    }
    public Task<FileCollection> CreateCollection(string name,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        name=CollectionName(name);string id=Guid.NewGuid().ToString("N");
        if(Scalar(c,"SELECT count(*) FROM Collections")>=512)throw new InvalidOperationException("最多保留 512 个收藏夹，请先整理已有收藏夹。");
        try{Execute(c,"INSERT INTO Collections VALUES($id,$name,$key,$now)",("$id",id),("$name",name),("$key",name.ToUpperInvariant()),("$now",DateTime.UtcNow.Ticks));}
        catch(SqliteException ex)when(ex.SqliteErrorCode==19){throw new ArgumentException("同名收藏夹已存在。",ex);}
        return new FileCollection(id,name,0);
    },cancellation);
    public Task<bool> RenameCollection(string id,string name,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        name=CollectionName(name);
        try{return Execute(c,"UPDATE Collections SET name=$name,name_key=$key WHERE collection_id=$id",("$id",id),("$name",name),("$key",name.ToUpperInvariant()))==1;}
        catch(SqliteException ex)when(ex.SqliteErrorCode==19){throw new ArgumentException("同名收藏夹已存在。",ex);}
    },cancellation);
    public Task<bool> DeleteCollection(string id,CancellationToken cancellation=default)=>writer.Execute(c=>Execute(c,"DELETE FROM Collections WHERE collection_id=$id",("$id",id))==1,cancellation);
    public Task<string> RelativeCollectionDirectory(string id,string path,CancellationToken cancellation=default)=>interactiveReader.Execute(c=>
    {
        using var command=c.CreateCommand();command.CommandText="SELECT DISTINCT r.display_path FROM Roots r JOIN Files f ON f.root_id=r.root_id JOIN CollectionMembers m ON m.location_key=f.location_key AND m.directory_location_id=(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=f.directory_id) WHERE m.collection_id=$id ORDER BY length(r.display_path) DESC";command.Parameters.AddWithValue("$id",id);
        using var rows=command.ExecuteReader();while(rows.Read())
        {
            cancellation.ThrowIfCancellationRequested();string relative=Path.GetRelativePath(rows.GetString(0),path);
            if(relative!="."&&!Path.IsPathRooted(relative)&&!relative.Split('\\','/').Contains(".."))return relative;
        }
        throw new ArgumentException("请选择某个收藏来源目录内的子文件夹。");
    },cancellation);
    public Task<IReadOnlyList<FileCollection>> ReadCollections(CancellationToken cancellation=default)=>interactiveReader.Execute<IReadOnlyList<FileCollection>>(c=>
    {
        using var command=c.CreateCommand();command.CommandText="SELECT c.collection_id,c.name,(SELECT count(*) FROM CollectionMembers m WHERE m.collection_id=c.collection_id) FROM Collections c ORDER BY c.name_key LIMIT 513";
        using var rows=command.ExecuteReader();var result=new List<FileCollection>();while(rows.Read())result.Add(new(rows.GetString(0),rows.GetString(1),rows.GetInt64(2)));
        if(result.Count>512)throw new InvalidDataException("收藏夹数量超过 512 个，请先整理收藏夹。");return result;
    },cancellation);
    public Task<int> ChangeCollectionMembers(IReadOnlyList<string> collectionIds,IReadOnlyList<string> entryIds,bool add,CancellationToken cancellation=default)=>ChangeCollectionMembersCore(collectionIds,entryIds,null,add,cancellation);
    public Task<int> ChangeCollectionItems(IReadOnlyList<string> collectionIds,IReadOnlyList<SnapshotItem> items,bool add,CancellationToken cancellation=default)=>ChangeCollectionMembersCore(collectionIds,items.Select(i=>i.EntryId).ToArray(),items,add,cancellation);
    public Task<int> RemoveItemFromCollections(SnapshotItem item,CancellationToken cancellation=default)=>ChangeCollectionMembersCore([],[item.EntryId],[item],false,cancellation,allCollections:true);
    private Task<int> ChangeCollectionMembersCore(IReadOnlyList<string> collectionIds,IReadOnlyList<string> entryIds,IReadOnlyList<SnapshotItem>? observed,bool add,CancellationToken cancellation,bool allCollections=false)=>writer.Execute(c=>
    {
        if((!allCollections&&(collectionIds.Count is <1 or >64))||entryIds.Count>256||(allCollections&&add))throw new ArgumentException("收藏批次大小无效。");
        using var transaction=c.BeginTransaction();int changed=0;
        using(var check=c.CreateCommand())
        {
            check.Transaction=transaction;
            check.CommandText="SELECT f.entry_state,f.file_version,f.path_revision,f.relative_path,f.directory_id,f.root_id,r.display_path,r.root_epoch,b.location_id,b.binding_revision,l.state FROM Files f JOIN Roots r ON r.root_id=f.root_id JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id WHERE f.entry_id=$entry";
            check.Parameters.AddWithValue("$entry","");
            foreach(string entry in entryIds.Distinct())
            {
                cancellation.ThrowIfCancellationRequested();check.Parameters["$entry"].Value=entry;using var row=check.ExecuteReader();
                if(!row.Read()||(add&&(row.GetString(0)=="missing"||row.GetString(10)!="active")))throw StaleCollectionSelection();
                if(observed is not null)foreach(var item in observed.Where(i=>i.EntryId==entry))
                    if(row.GetInt64(1)!=item.Version||row.GetInt64(2)!=item.PathRevision||row.GetString(3)!=item.RelativePath||row.GetString(4)!=item.DirectoryId||row.GetString(5)!=item.SourceRootId||row.GetString(6)!=item.SourceRootPath||row.GetInt64(7)!=item.SourceRootEpoch||row.GetString(8)!=item.DirectoryLocationId||row.GetInt64(9)!=item.BindingRevision)throw StaleCollectionSelection();
            }
        }
        using var command=c.CreateCommand();command.Transaction=transaction;
        command.CommandText=add?"""
            INSERT INTO CollectionMembers(collection_id,location_key,entry_id,added_utc_ticks,directory_location_id)
            SELECT $collection,f.location_key,f.entry_id,$now,b.location_id FROM Files f JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id WHERE f.entry_id=$entry
            ON CONFLICT(collection_id,directory_location_id,location_key) DO NOTHING
            """:"DELETE FROM CollectionMembers WHERE "+(allCollections?"":"collection_id=$collection AND ")+"(directory_location_id,location_key)=(SELECT b.location_id,f.location_key FROM Files f JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id WHERE f.entry_id=$entry)";
        command.Parameters.AddWithValue("$collection","");command.Parameters.AddWithValue("$entry","");if(add)command.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
        foreach(string collection in allCollections?new[]{""}:collectionIds.Distinct())foreach(string entry in entryIds.Distinct())
        {cancellation.ThrowIfCancellationRequested();command.Parameters["$collection"].Value=collection;command.Parameters["$entry"].Value=entry;changed+=command.ExecuteNonQuery();}
        cancellation.ThrowIfCancellationRequested();transaction.Commit();return changed;
    },cancellation);
    internal const string CollectionSelectionRowsSql="FROM json_each($ranges) j CROSS JOIN collection_selection.ResultItems i CROSS JOIN Files f CROSS JOIN DirectoryLocationBindings b WHERE b.directory_id=f.directory_id AND i.session_id=$session AND i.ordinal>=json_extract(j.value,'$.Start') AND i.ordinal<json_extract(j.value,'$.Start')+json_extract(j.value,'$.Count') AND f.entry_id=i.entry_id";
    private static IOException StaleCollectionSelection()=>new("所选文件已更改或不存在，请刷新结果后重新选择；本次收藏修改未提交。");
    public Task<int> ChangeCollectionSelection(string[] collectionIds,string sessionId,IReadOnlyList<OrdinalRange> ranges,bool add,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        if(collectionIds.Length is <1 or >64||ranges.Count>8192)throw new ArgumentException("收藏选择范围无效。");
        Execute(c,"ATTACH DATABASE $path AS collection_selection",("$path",sessionPath));
        try
        {
            using var transaction=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=transaction;
            command.CommandText="SELECT count(*) FROM collection_selection.ResultSessions WHERE session_id=$session AND state='ready' AND active_leases>0";command.Parameters.AddWithValue("$session",sessionId);
            if((long)command.ExecuteScalar()!!=1)throw new IOException("所选结果已关闭，请重新选择。");
            command.CommandText="SELECT coalesce(max(ordinal)+1,0) FROM collection_selection.ResultItems WHERE session_id=$session";
            long total=(long)command.ExecuteScalar()!;
            command.Parameters.AddWithValue("$ranges",System.Text.Json.JsonSerializer.Serialize(OrdinalSelection.Normalize(ranges,total)));command.Parameters.AddWithValue("$collection","");command.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
            command.Parameters.AddWithValue("$add",add?1:0);
            command.CommandText="""
                SELECT 1 FROM json_each($ranges) j CROSS JOIN collection_selection.ResultItems i
                LEFT JOIN Files f ON f.entry_id=i.entry_id LEFT JOIN Roots r ON r.root_id=f.root_id LEFT JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id LEFT JOIN DirectoryLocations l ON l.location_id=b.location_id
                WHERE i.session_id=$session AND i.ordinal>=json_extract(j.value,'$.Start') AND i.ordinal<json_extract(j.value,'$.Start')+json_extract(j.value,'$.Count')
                AND (f.entry_id IS NULL OR ($add=1 AND (f.entry_state='missing' OR l.state IS NOT 'active')) OR b.location_id IS NOT i.observed_directory_location_id OR b.binding_revision IS NOT i.observed_binding_revision OR f.file_version IS NOT i.observed_version OR f.path_revision IS NOT i.observed_path_revision OR f.relative_path IS NOT i.snapshot_relative_path OR f.directory_id IS NOT i.directory_id OR f.root_id IS NOT i.source_root_id OR r.display_path IS NOT i.source_root_path OR r.root_epoch IS NOT i.source_root_epoch) LIMIT 1
                """;
            if(command.ExecuteScalar() is not null)throw StaleCollectionSelection();
            command.CommandText="CREATE TEMP TABLE SelectedCollectionLocations(location_key TEXT NOT NULL,entry_id TEXT NOT NULL,directory_location_id TEXT NOT NULL,PRIMARY KEY(directory_location_id,location_key)); INSERT INTO temp.SelectedCollectionLocations SELECT f.location_key,min(f.entry_id),b.location_id "+CollectionSelectionRowsSql+" GROUP BY b.location_id,f.location_key";
            command.ExecuteNonQuery();
            command.CommandText=add?"INSERT INTO CollectionMembers SELECT $collection,location_key,entry_id,$now,directory_location_id FROM temp.SelectedCollectionLocations WHERE true ON CONFLICT(collection_id,directory_location_id,location_key) DO NOTHING":"DELETE FROM CollectionMembers WHERE collection_id=$collection AND (directory_location_id,location_key) IN(SELECT directory_location_id,location_key FROM temp.SelectedCollectionLocations)";
            int changed=0;foreach(string id in collectionIds.Distinct()){cancellation.ThrowIfCancellationRequested();command.Parameters["$collection"].Value=id;changed+=command.ExecuteNonQuery();}
            command.CommandText="DROP TABLE temp.SelectedCollectionLocations";command.ExecuteNonQuery();
            cancellation.ThrowIfCancellationRequested();transaction.Commit();return changed;
        }
        finally{Execute(c,"DETACH DATABASE collection_selection");}
    },cancellation);
}
