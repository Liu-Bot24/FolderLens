using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed record FileCollection(string Id,string Name,long Count);

public sealed partial class CatalogStore
{
    internal static string LocationKey(string root,string relative,string caseMode)
    {
        string path=Path.Combine(root,relative).Replace('/','\\');
        return caseMode=="sensitive"?path:path.ToUpperInvariant();
    }
    private static void MigrateCollections(SqliteConnection c,string name,bool existing)
    {
        if(existing)
        {
            using var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=c.DataSource+".pre-v4-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+".bak",Pooling=false}.ToString());
            backup.Open();c.BackupDatabase(backup);
        }
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
    private static void RepairCollectionDirectoryTrigger(SqliteConnection connection)
    {
        using var command=connection.CreateCommand();
        command.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='Directories_Location_Update'";
        if(command.ExecuteScalar() is string sql&&sql.Contains("root_id=NEW.root_id AND directory_id=NEW.directory_id",StringComparison.Ordinal))return;
        // A trigger-only v4 repair: no data rewrite or format change. The DDL is
        // atomic, remains readable by v4 builds, and reuses the existing index.
        using var transaction=connection.BeginTransaction();command.Transaction=transaction;
        command.CommandText="""
            DROP TRIGGER IF EXISTS Directories_Location_Update;
            CREATE TRIGGER Directories_Location_Update AFTER UPDATE OF case_mode ON Directories WHEN OLD.case_mode<>NEW.case_mode BEGIN
                UPDATE Files SET location_key=lens_location((SELECT display_path FROM Roots WHERE root_id=Files.root_id),relative_path,NEW.case_mode) WHERE root_id=NEW.root_id AND directory_id=NEW.directory_id;
            END;
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
        using var command=c.CreateCommand();command.CommandText="SELECT DISTINCT r.display_path FROM Roots r JOIN Files f ON f.root_id=r.root_id JOIN CollectionMembers m ON m.location_key=f.location_key WHERE m.collection_id=$id ORDER BY length(r.display_path) DESC";command.Parameters.AddWithValue("$id",id);
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
    public Task<int> ChangeCollectionMembers(IReadOnlyList<string> collectionIds,IReadOnlyList<string> entryIds,bool add,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        if(collectionIds.Count is <1 or >64||entryIds.Count>256)throw new ArgumentException("收藏批次大小无效。");
        using var transaction=c.BeginTransaction();int changed=0;
        using var command=c.CreateCommand();command.Transaction=transaction;
        command.CommandText=add?"""
            INSERT INTO CollectionMembers(collection_id,location_key,entry_id,added_utc_ticks)
            SELECT $collection,location_key,entry_id,$now FROM Files WHERE entry_id=$entry
            ON CONFLICT(collection_id,location_key) DO NOTHING
            """:"DELETE FROM CollectionMembers WHERE collection_id=$collection AND location_key=(SELECT location_key FROM Files WHERE entry_id=$entry)";
        command.Parameters.AddWithValue("$collection","");command.Parameters.AddWithValue("$entry","");if(add)command.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
        foreach(string collection in collectionIds.Distinct())foreach(string entry in entryIds.Distinct())
        {cancellation.ThrowIfCancellationRequested();command.Parameters["$collection"].Value=collection;command.Parameters["$entry"].Value=entry;changed+=command.ExecuteNonQuery();}
        cancellation.ThrowIfCancellationRequested();transaction.Commit();return changed;
    },cancellation);
    public Task<int> ChangeCollectionSelection(string[] collectionIds,string sessionId,IReadOnlyList<OrdinalRange> ranges,bool add,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        if(collectionIds.Length is <1 or >64||ranges.Count>8192)throw new ArgumentException("收藏选择范围无效。");
        Execute(c,"ATTACH DATABASE $path AS collection_selection",("$path",sessionPath));
        try
        {
            using var transaction=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=transaction;
            command.CommandText="SELECT count(*) FROM collection_selection.ResultSessions WHERE session_id=$session AND state='ready' AND active_leases>0";command.Parameters.AddWithValue("$session",sessionId);
            if((long)command.ExecuteScalar()!!=1)throw new IOException("所选结果已关闭，请重新选择。");
            string selectedRows="FROM collection_selection.ResultItems i JOIN Files f ON i.entry_id=f.entry_id WHERE i.session_id=$session AND EXISTS(SELECT 1 FROM json_each($ranges) j WHERE i.ordinal>=json_extract(j.value,'$.Start') AND i.ordinal<json_extract(j.value,'$.Start')+json_extract(j.value,'$.Count'))";
            string selected="SELECT f.location_key "+selectedRows;
            command.Parameters.AddWithValue("$ranges",System.Text.Json.JsonSerializer.Serialize(ranges));command.Parameters.AddWithValue("$collection","");command.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);
            command.CommandText=add?$"INSERT INTO CollectionMembers SELECT $collection,f.location_key,min(f.entry_id),$now {selectedRows} GROUP BY f.location_key ON CONFLICT(collection_id,location_key) DO NOTHING":$"DELETE FROM CollectionMembers WHERE collection_id=$collection AND location_key IN({selected})";
            int changed=0;foreach(string id in collectionIds.Distinct()){cancellation.ThrowIfCancellationRequested();command.Parameters["$collection"].Value=id;changed+=command.ExecuteNonQuery();}
            cancellation.ThrowIfCancellationRequested();transaction.Commit();return changed;
        }
        finally{Execute(c,"DETACH DATABASE collection_selection");}
    },cancellation);
}
