using System.Reflection;
using System.Text.Json;
using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed record FileRecord(string EntryId,string RootId,string DirectoryId,string RelativePath,string Name,string Kind,long Version,long Bytes,long? Allocated,long Modified,string? Format,long? Width,long? Height,bool? Raw,bool? Animated);
public sealed record ResultHandle(string Id,string RootId,long Epoch,long Generation,long Count,long Pending,long Unresolvable)
{
    public string? CollectionId {get;init;}
    public long ConfirmedMatchCount { get; init; } = Count;
    public bool IsPendingView { get; init; }
}
public sealed record SnapshotItem(long Ordinal,string EntryId,long Version,string RelativePath,string DirectoryId,long Bytes,long? Allocated,string Kind)
{
    public SnapshotGroup? Group {get;init;}
    public string? SourceRootId {get;init;}
    public string? SourceRootPath {get;init;}
    public long? SourceRootEpoch {get;init;}
}
public sealed record SnapshotGroup(string Id,string RelativePath,long Bytes,long MatchCount,long Start,long Count,string ScanState,string CapacityScope);
public sealed record FirstResultPage(IReadOnlyList<SnapshotItem> Items, long CatalogRevision, bool IsPendingView);
public sealed record SnapshotLimits
{
    public TimeSpan SoftDeadline { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HardDeadline { get; init; } = TimeSpan.FromSeconds(30);
    public long CatalogWalBytes { get; init; } = 256L << 20;
    public long SessionDiskBytes { get; init; } = 1L << 30;
    public int HistoryCount { get; init; } = 2;
}
public sealed record WalCheckpoint(int Busy, int LogFrames, int CheckpointedFrames, long Bytes);

public sealed partial class CatalogStore : IAsyncDisposable
{
    private readonly DatabaseExecutor writer;
    private readonly DatabaseExecutor reader;
    private readonly DatabaseExecutor interactiveReader;
    private readonly DatabaseExecutor sessionReader;
    private readonly string sessionPath;
    private readonly string catalogPath;
    private readonly SnapshotLimits limits;
    private readonly SemaphoreSlim snapshotGate = new(1,1);
    private string? activeSessionId;
    private int snapshotUnderPressure;
    public bool SnapshotUnderPressure => Volatile.Read(ref snapshotUnderPressure) != 0;
    public CatalogStore(string dataDirectory) : this(dataDirectory, new SnapshotLimits()) { }
    public CatalogStore(string dataDirectory, SnapshotLimits limits)
    {
        if(limits.HardDeadline <= TimeSpan.Zero || limits.SoftDeadline < TimeSpan.Zero || limits.SoftDeadline > limits.HardDeadline || limits.CatalogWalBytes < 4096 || limits.SessionDiskBytes < 262144 || limits.HistoryCount is <0 or >2) throw new ArgumentException("快照预算无效。",nameof(limits));
        this.limits=limits;
        Directory.CreateDirectory(dataDirectory);
        catalogPath=Path.Combine(dataDirectory,"catalog.sqlite");
        writer=new(catalogPath);
        reader=new(catalogPath);
        interactiveReader=new(catalogPath);
        sessionPath=Path.Combine(dataDirectory,"sessions.sqlite");
        sessionReader=new(sessionPath);
    }
    public async Task Initialize(CancellationToken cancellation=default)
    {
        await writer.Execute(c=>InitializeSchema(c,"catalog"),cancellation);
        await sessionReader.Execute(c=>InitializeSchema(c,"sessions"),cancellation);
        await sessionReader.Execute(c=>Execute(c,"UPDATE ResultSessions SET active_leases=0; UPDATE ResultSessions SET state='failed',error_code='Interrupted',completed_utc_ticks=$now WHERE state='building'",("$now",DateTime.UtcNow.Ticks)),cancellation);
    }
    private static bool InitializeSchema(SqliteConnection c,string name)
    {
        using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version";long version=(long)cmd.ExecuteScalar()!;
        long supported=4;
        if(version>supported)throw new InvalidDataException("数据库由较新版本创建，请使用匹配版本。");
        bool existing=version!=0;
        if(version==0)
        {
            var assembly=typeof(CatalogStore).Assembly;
            string resource=assembly.GetManifestResourceNames().Single(n=>n.EndsWith(name+".sql",StringComparison.Ordinal));
            using var stream=assembly.GetManifestResourceStream(resource)!;using var text=new StreamReader(stream);
            cmd.CommandText=text.ReadToEnd();cmd.ExecuteNonQuery();version=1;
        }
        if(name=="catalog"&&version==1)
        {
            if(existing)
            {
                string backup=c.DataSource+".pre-v2-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",System.Globalization.CultureInfo.InvariantCulture)+".bak";
                using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString());destination.Open();c.BackupDatabase(destination);
            }
            using var migration=c.BeginTransaction();cmd.Transaction=migration;cmd.CommandText="""
                CREATE TABLE FileDetails(entry_id TEXT PRIMARY KEY REFERENCES Files(entry_id) ON DELETE CASCADE,source_version INTEGER NOT NULL CHECK(source_version>=1),provider_version TEXT NOT NULL,detail_json TEXT NOT NULL CHECK(length(CAST(detail_json AS BLOB))<=65536),updated_utc_ticks INTEGER NOT NULL) STRICT;
                UPDATE SchemaInfo SET schema_version=2;
                PRAGMA user_version=2;
                """;cmd.ExecuteNonQuery();migration.Commit();
            version=2;
        }
        if(name=="catalog"&&version==2)
        {
            if(existing)
            {
                string backup=c.DataSource+".pre-v3-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",System.Globalization.CultureInfo.InvariantCulture)+".bak";
                using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString());destination.Open();c.BackupDatabase(destination);
            }
            using var migration=c.BeginTransaction();cmd.Transaction=migration;cmd.CommandText="""
                UPDATE Files SET kind='other' WHERE kind='image' AND page_count>1 AND is_raw IS NOT 1 AND is_animated IS NOT 1;
                UPDATE SchemaInfo SET schema_version=3,catalog_revision=catalog_revision+1;
                PRAGMA user_version=3;
                """;cmd.ExecuteNonQuery();migration.Commit();version=3;
        }
        if(name=="sessions"&&version==1)
        {
            if(existing)
            {
                string backup=c.DataSource+".pre-v2-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",System.Globalization.CultureInfo.InvariantCulture)+".bak";
                using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString());destination.Open();c.BackupDatabase(destination);
            }
            using var migration=c.BeginTransaction();cmd.Transaction=migration;cmd.CommandText="""
                ALTER TABLE ResultItems ADD COLUMN snapshot_kind TEXT NOT NULL DEFAULT 'other' CHECK(snapshot_kind IN('image','video','audio','text','markdown','other'));
                UPDATE ResultSessions SET state='failed',error_code='SnapshotSchemaChanged',active_leases=0 WHERE state IN('building','ready');
                PRAGMA user_version=2;
                """;cmd.ExecuteNonQuery();migration.Commit();
            version=2;
        }
        if(name=="sessions"&&version==2)
        {
            if(existing)
            {
                string backup=c.DataSource+".pre-v3-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",System.Globalization.CultureInfo.InvariantCulture)+".bak";
                using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Pooling=false}.ToString());destination.Open();c.BackupDatabase(destination);
            }
            using var migration=c.BeginTransaction();cmd.Transaction=migration;cmd.CommandText="""
                ALTER TABLE ResultItems ADD COLUMN group_id TEXT;
                CREATE TABLE ResultGroups(
                    session_id TEXT NOT NULL REFERENCES ResultSessions(session_id) ON DELETE CASCADE,
                    group_id TEXT NOT NULL,relative_path TEXT NOT NULL,logical_bytes INTEGER NOT NULL CHECK(logical_bytes>=0),
                    match_count INTEGER NOT NULL CHECK(match_count>=0),start_ordinal INTEGER NOT NULL CHECK(start_ordinal>=0),
                    item_count INTEGER NOT NULL DEFAULT 0 CHECK(item_count>=0),scan_state TEXT NOT NULL,capacity_scope TEXT NOT NULL,
                    PRIMARY KEY(session_id,group_id),UNIQUE(session_id,start_ordinal)
                ) STRICT;
                PRAGMA user_version=3;
                """;cmd.ExecuteNonQuery();migration.Commit();version=3;
        }
        if(version==3)MigrateCollections(c,name,existing);
        return true;
    }
    public Task<long> OpenRoot(string rootId,string path,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        using var cmd=c.CreateCommand();cmd.CommandText="""
        INSERT INTO Roots(root_id,display_path,canonical_key,root_epoch,availability,scan_state) VALUES($id,$path,$path,1,'online','notStarted')
        ON CONFLICT(root_id) DO UPDATE SET root_epoch=root_epoch+1,display_path=excluded.display_path RETURNING root_epoch;
        """;cmd.Parameters.AddWithValue("$id",rootId);cmd.Parameters.AddWithValue("$path",path);return (long)cmd.ExecuteScalar()!;
    },cancellation);
    public Task<int> SeedBenchmark(int count,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        c.CreateFunction("lens_natural",(string s)=>NaturalOrder.Key(s),true);
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
        INSERT OR IGNORE INTO Roots(root_id,display_path,canonical_key,root_epoch,availability,scan_state) VALUES('benchmark','fixture','fixture',1,'online','ready');
        INSERT OR IGNORE INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES('benchmark-dir','benchmark','','','','sensitive');
        WITH RECURSIVE nums(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM nums WHERE n<$count)
        INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,mtime_utc_ticks,updated_revision)
        SELECT printf('%012d',n),'benchmark','benchmark-dir','file'||n||'.jpg','.jpg','file'||n||'.jpg','file'||n||'.jpg',CAST(printf('%012d',n) AS BLOB),lens_natural('file'||n||'.jpg'),1,'synthetic','image','extension',n*1024,1,0 FROM nums;
        """;cmd.Parameters.AddWithValue("$count",count);return cmd.ExecuteNonQuery();
    },cancellation);

    public Task<FirstResultPage> ReadFirstPage(FilterSpec filter,CancellationToken cancellation=default)=>interactiveReader.Execute(c=>
    {
        FilterQuery query=FilterSql.Build(filter); RegisterFunctions(c);
        using var transaction=c.BeginTransaction(deferred:true);
        using var revision=c.CreateCommand();revision.Transaction=transaction;revision.CommandText="SELECT catalog_revision FROM SchemaInfo";long rev=(long)revision.ExecuteScalar()!;
        using var grouping=filter.Grouping.Enabled?new FolderGroupingQuery(c,transaction,filter,cancellation):null;
        using var cmd=c.CreateCommand();cmd.Transaction=transaction;
        string predicate=filter.IncludePending ? $"({query.CandidateExpression}) AND ({query.StateExpression})='Pending'" : query.MatchExpression;
        cmd.CommandText=$"SELECT f.entry_id,f.file_version,f.relative_path,f.directory_id,f.logical_bytes,f.allocated_bytes,f.kind,f.root_id,r.display_path,r.root_epoch FROM Files f JOIN Roots r ON r.root_id=f.root_id {(grouping is null?"":FolderGroupingQuery.Join)} WHERE {predicate} ORDER BY {(grouping is null?"":FolderGroupingQuery.OrderPrefix)}{query.OrderBy} LIMIT 256";
        foreach(var p in query.Parameters)cmd.Parameters.AddWithValue(p.Key,p.Value);
        using var rows=cmd.ExecuteReader();var items=new List<SnapshotItem>(256);
        while(rows.Read()){cancellation.ThrowIfCancellationRequested();items.Add(new(items.Count,rows.GetString(0),rows.GetInt64(1),rows.GetString(2),rows.GetString(3),rows.GetInt64(4),rows.IsDBNull(5)?null:rows.GetInt64(5),rows.GetString(6)){SourceRootId=rows.GetString(7),SourceRootPath=rows.GetString(8),SourceRootEpoch=rows.GetInt64(9)});}
        rows.Close();transaction.Commit();return new FirstResultPage(items,rev,filter.IncludePending);
    },cancellation);

    public Task<DirectoryListing> ReadChildDirectories(string rootId,string relativeParent,string basePath,string storage,CancellationToken cancellation=default)=>interactiveReader.Execute(c=>
    {
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT child.relative_path FROM Directories child JOIN Directories parent ON parent.directory_id=child.parent_id AND parent.root_id=child.root_id WHERE child.root_id=$root AND parent.relative_path=$parent AND child.entry_state='present'";
        cmd.Parameters.AddWithValue("$root",rootId);cmd.Parameters.AddWithValue("$parent",relativeParent.Replace('/','\\'));
        using var rows=cmd.ExecuteReader();IEnumerable<string> Paths(){while(rows.Read()){cancellation.ThrowIfCancellationRequested();yield return Path.Combine(basePath,rows.GetString(0));}}return DirectoryListing.Create(storage,Paths(),cancellation);
    },cancellation);

    public Task<long?> FindOrdinal(string snapshotId,string relativePath,CancellationToken cancellation=default)=>sessionReader.Execute<long?>(c=>
    {
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT ordinal FROM ResultItems WHERE session_id=$id AND (snapshot_relative_path=$path OR lens_location(source_root_path,snapshot_relative_path,'sensitive')=$path) ORDER BY ordinal LIMIT 1";cmd.Parameters.AddWithValue("$id",snapshotId);cmd.Parameters.AddWithValue("$path",relativePath.Replace('/','\\'));object? value=cmd.ExecuteScalar();return value is long ordinal?ordinal:null;
    },cancellation);

    private static void RegisterFunctions(SqliteConnection connection)
    {
        connection.CreateFunction("lens_fold",(string value)=>value.ToUpperInvariant(),true);
        string? previous=null;DirectoryRuleSet? compiled=null;
        connection.CreateFunction("lens_directory_visible",(string path,string json)=>
        {
            if(previous!=json){compiled=new DirectoryRuleSet(System.Text.Json.JsonSerializer.Deserialize<DirectoryRule[]>(json)!);previous=json;}
            return compiled!.IsVisible(path);
        },true);
    }
    private static long FileBytes(string path)=>File.Exists(path)?new FileInfo(path).Length:0;
    private long SessionBytes()=>FileBytes(sessionPath)+FileBytes(sessionPath+"-wal")+FileBytes(sessionPath+"-shm");
    private static long Scalar(SqliteConnection c,string sql){using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture);}
    private static int Execute(SqliteConnection c,string sql,params (string Name,object Value)[] values)
    {
        using var cmd=c.CreateCommand();cmd.CommandText=sql;foreach(var value in values)cmd.Parameters.AddWithValue(value.Name,value.Value);return cmd.ExecuteNonQuery();
    }
    public Task<WalCheckpoint> CheckpointCatalog(bool truncate=false,CancellationToken cancellation=default)=>writer.Execute(c=>Checkpoint(c,catalogPath,truncate),cancellation);
    private static WalCheckpoint Checkpoint(SqliteConnection c,string path,bool truncate)
    {
        using var cmd=c.CreateCommand();cmd.CommandText=truncate?"PRAGMA wal_checkpoint(TRUNCATE)":"PRAGMA wal_checkpoint(PASSIVE)";
        using var result=cmd.ExecuteReader();result.Read();return new(result.GetInt32(0),result.GetInt32(1),result.GetInt32(2),FileBytes(path+"-wal"));
    }

    // Explicit references protect a displayed or exported historical session. The most
    // recently published session is also protected until the next successful publication.
    public Task<bool> RetainSnapshot(string sessionId,CancellationToken cancellation=default)=>sessionReader.Execute(c=>
        Execute(c,"UPDATE ResultSessions SET active_leases=active_leases+1 WHERE session_id=$id AND state='ready'",("$id",sessionId))==1,cancellation);
    public async Task<bool> ReleaseSnapshot(string sessionId,CancellationToken cancellation=default)
    {
        await snapshotGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            bool released=await sessionReader.Execute(c=>Execute(c,"UPDATE ResultSessions SET active_leases=active_leases-1 WHERE session_id=$id AND active_leases>0",("$id",sessionId))==1,cancellation).ConfigureAwait(false);
            if(activeSessionId==sessionId){activeSessionId=null;released=true;}return released;
        }
        finally { snapshotGate.Release(); }
    }

    public async Task<ResultHandle> CreateSnapshot(FilterSpec filter,long epoch,long generation,CancellationToken cancellation=default)
    {
        filter.Validate();
        await snapshotGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Fixture seeding may leave a large reusable WAL. Release it before starting
            // the measured read lease; growth under this lease is the actual hazard.
            await CheckpointCatalog(true,cancellation).ConfigureAwait(false);
            return await reader.Execute(c=>BuildSnapshot(c,filter,epoch,generation,cancellation),cancellation).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref snapshotUnderPressure,0);snapshotGate.Release(); }
    }

    private ResultHandle BuildSnapshot(SqliteConnection c,FilterSpec filter,long epoch,long generation,CancellationToken cancellation)
    {
        FilterQuery query=FilterSql.Build(filter);RegisterFunctions(c);
        using var session=DatabaseExecutor.Open(sessionPath);
        PruneSessions(session,limits.HistoryCount);
        // SQLite's page limit includes the freelist, so deleted histories are reused.
        // Reserve bounded WAL/shm headroom separately from the main database budget.
        long reserve=Math.Min(16L<<20,limits.SessionDiskBytes/4),maxPages=(limits.SessionDiskBytes-reserve)/Scalar(session,"PRAGMA page_size");
        long actualPages=Scalar(session,$"PRAGMA max_page_count={maxPages}");
        if(actualPages>maxPages || SessionBytes()>limits.SessionDiskBytes)throw new IOException("结果会话空间不足；活动会话已保留。");
        string id=Guid.NewGuid().ToString("N");long count=0,matched=0,pending=0,unresolvable=0;
        bool started=false;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        int resourceFault=0;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        using var deadline= new Timer(_=>
        {
            int fault=clock.Elapsed>=limits.HardDeadline?1:FileBytes(catalogPath+"-wal")>=limits.CatalogWalBytes?2:SessionBytes()>limits.SessionDiskBytes?3:0;
            if(clock.Elapsed>=limits.SoftDeadline || FileBytes(catalogPath+"-wal")>=limits.CatalogWalBytes*3/4)Volatile.Write(ref snapshotUnderPressure,1);
            if(fault!=0){Interlocked.CompareExchange(ref resourceFault,fault,0);stop.Cancel();}
        },null,TimeSpan.Zero,TimeSpan.FromMilliseconds(20));
        using var interrupt=stop.Token.Register(()=>{SQLitePCL.raw.sqlite3_interrupt(c.Handle);SQLitePCL.raw.sqlite3_interrupt(session.Handle);});
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            using var readTransaction=c.BeginTransaction(deferred:true);
            using var revision=c.CreateCommand();revision.Transaction=readTransaction;revision.CommandText="SELECT catalog_revision FROM SchemaInfo";long rev=(long)revision.ExecuteScalar()!;
            using var grouping=filter.Grouping.Enabled?new FolderGroupingQuery(c,readTransaction,filter,stop.Token):null;
            Execute(session,"INSERT INTO ResultSessions(session_id,root_id,root_epoch,query_generation,catalog_revision,filter_json,sort_json,natural_key_version,state,created_utc_ticks) VALUES($id,$root,$epoch,$gen,$rev,$filter,$sort,1,'building',$now)",
                ("$id",id),("$root",filter.RootId),("$epoch",epoch),("$gen",generation),("$rev",rev),("$filter",JsonSerializer.Serialize(filter)),("$sort",JsonSerializer.Serialize(filter.Sort)),("$now",DateTime.UtcNow.Ticks));
            started=true;
            using(var dirs=c.CreateCommand())
            {
                dirs.Transaction=readTransaction;dirs.CommandText="SELECT directory_id,parent_id,relative_path FROM Directories WHERE root_id=$root";dirs.Parameters.AddWithValue("$root",filter.RootId);
                using var rows=dirs.ExecuteReader();SqliteTransaction? write=null;int batch=0;
                try
                {
                    while(rows.Read())
                    {
                        stop.Token.ThrowIfCancellationRequested();write??=session.BeginTransaction();
                        using var insert=session.CreateCommand();insert.Transaction=write;insert.CommandText="INSERT INTO ResultDirectories VALUES($id,$dir,$parent,$path)";
                        insert.Parameters.AddWithValue("$id",id);insert.Parameters.AddWithValue("$dir",rows.GetString(0));insert.Parameters.AddWithValue("$parent",rows.GetValue(1));insert.Parameters.AddWithValue("$path",rows.GetString(2));insert.ExecuteNonQuery();
                        if(++batch%512==0){write.Commit();write.Dispose();write=null;}
                    }
                    write?.Commit();
                }
                finally{write?.Dispose();}
            }
            using var read=c.CreateCommand();read.Transaction=readTransaction;
            string groupColumns=grouping is null?"NULL,NULL,NULL,NULL":"owner.id,owner.path,CASE WHEN owner.depth=0 THEN owner.direct_bytes ELSE owner.bytes END,CASE WHEN owner.depth=0 THEN owner.direct_matches ELSE owner.matches END";
            read.CommandText=$"SELECT f.entry_id,f.file_version,f.path_revision,f.directory_id,f.relative_path,f.logical_bytes,f.allocated_bytes,f.physical_identity,f.kind,{query.StateExpression} AS match_state,{groupColumns},f.root_id,r.display_path,r.root_epoch FROM Files f JOIN Roots r ON r.root_id=f.root_id {(grouping is null?"":FolderGroupingQuery.Join)} WHERE {query.CandidateExpression} ORDER BY {(grouping is null?"":FolderGroupingQuery.OrderPrefix)}{query.OrderBy}";
            foreach(var p in query.Parameters)read.Parameters.AddWithValue(p.Key,p.Value);
            using var source=read.ExecuteReader();
            using var add=session.CreateCommand();add.CommandText="INSERT INTO ResultItems(session_id,ordinal,entry_id,observed_version,observed_path_revision,directory_id,snapshot_relative_path,snapshot_logical_bytes,snapshot_allocated_bytes,snapshot_physical_identity,snapshot_kind,group_id,source_root_id,source_root_path,source_root_epoch) VALUES($id,$ordinal,$entry,$version,$pathrev,$dir,$path,$bytes,$allocated,$physical,$kind,$group,$sourceId,$sourcePath,$sourceEpoch)";
            foreach(string key in new[]{"$id","$ordinal","$entry","$version","$pathrev","$dir","$path","$bytes","$allocated","$physical","$kind","$group","$sourceId","$sourcePath","$sourceEpoch"})add.Parameters.Add(new SqliteParameter(key,DBNull.Value));
            string? previousGroup=null;
            using var scan=c.CreateCommand();scan.Transaction=readTransaction;scan.CommandText="SELECT scan_state FROM Roots WHERE root_id=$root";scan.Parameters.AddWithValue("$root",filter.RootId);string scanState=scan.ExecuteScalar()?.ToString()??"unknown";
            add.Prepare();SqliteTransaction? transaction=null;long batchBytes=0;
            void CommitBatch()
            {
                if(transaction is null)return;
                using var progress=session.CreateCommand();progress.Transaction=transaction;progress.CommandText="UPDATE ResultSessions SET committed_count=$n,pending_count=$pending,unresolvable_count=$failed WHERE session_id=$id";
                progress.Parameters.AddWithValue("$n",count);progress.Parameters.AddWithValue("$pending",pending);progress.Parameters.AddWithValue("$failed",unresolvable);progress.Parameters.AddWithValue("$id",id);progress.ExecuteNonQuery();
                stop.Token.ThrowIfCancellationRequested();transaction.Commit();transaction.Dispose();transaction=null;batchBytes=0;
                if(FileBytes(sessionPath+"-wal")>reserve/2)Checkpoint(session,sessionPath,true);
                if(SessionBytes()>limits.SessionDiskBytes){Interlocked.CompareExchange(ref resourceFault,3,0);stop.Cancel();}
                stop.Token.ThrowIfCancellationRequested();
            }
            try
            {
                while(source.Read())
                {
                    stop.Token.ThrowIfCancellationRequested();
                    string state=source.GetString(9);
                    if(state=="Match")matched++;else if(state=="Pending")pending++;else if(state=="Unresolvable")unresolvable++;
                    if(state!=(filter.IncludePending?"Pending":"Match"))continue;
                    transaction??=session.BeginTransaction();add.Transaction=transaction;
                    add.Parameters[0].Value=id;add.Parameters[1].Value=count;
                    for(int i=0;i<9;i++)add.Parameters[i+2].Value=source.GetValue(i);
                    add.Parameters[11].Value=source.GetValue(10);for(int i=0;i<3;i++)add.Parameters[12+i].Value=source.GetValue(14+i);
                    if(grouping is not null&&source.GetString(10)!=previousGroup)
                    {
                        previousGroup=source.GetString(10);
                        using var group=session.CreateCommand();group.Transaction=transaction;
                        group.CommandText="INSERT INTO ResultGroups(session_id,group_id,relative_path,logical_bytes,match_count,start_ordinal,scan_state,capacity_scope) VALUES($session,$group,$path,$bytes,$matches,$start,$state,$scope)";
                        group.Parameters.AddWithValue("$session",id);group.Parameters.AddWithValue("$group",previousGroup);group.Parameters.AddWithValue("$path",source.GetString(11));group.Parameters.AddWithValue("$bytes",source.GetInt64(12));group.Parameters.AddWithValue("$matches",source.GetInt64(13));group.Parameters.AddWithValue("$start",count);group.Parameters.AddWithValue("$state",scanState);group.Parameters.AddWithValue("$scope",filter.Grouping.CapacityScope);group.ExecuteNonQuery();
                    }
                    add.ExecuteNonQuery();count++;batchBytes+=System.Text.Encoding.UTF8.GetByteCount(source.GetString(4))+256;
                    if(count%512==0 || batchBytes>=1<<20)CommitBatch();
                }
                CommitBatch();
            }
            finally{transaction?.Dispose();}
            source.Close();
            stop.Token.ThrowIfCancellationRequested();
            // Complete the catalog read before Ready. No cancelled or incomplete source
            // cursor can be promoted merely because its committed prefix is contiguous.
            readTransaction.Commit();
            stop.Token.ThrowIfCancellationRequested();
            using var finishTransaction=session.BeginTransaction();
            if(grouping is not null)
            {
                using var spans=session.CreateCommand();spans.Transaction=finishTransaction;
                spans.CommandText="WITH spans AS MATERIALIZED (SELECT group_id,start_ordinal,lead(start_ordinal,1,$count) OVER(ORDER BY start_ordinal)-start_ordinal AS size FROM ResultGroups WHERE session_id=$session) UPDATE ResultGroups AS g SET item_count=s.size FROM spans s WHERE g.session_id=$session AND g.group_id=s.group_id";
                spans.Parameters.AddWithValue("$count",count);spans.Parameters.AddWithValue("$session",id);spans.ExecuteNonQuery();
            }
            using var finish=session.CreateCommand();finish.Transaction=finishTransaction;finish.CommandText="""
                UPDATE ResultSessions SET state='ready',committed_count=$n,total_count=$n,pending_count=$pending,unresolvable_count=$failed,completed_utc_ticks=$now
                WHERE session_id=$id AND (SELECT count(*) FROM ResultItems WHERE session_id=$id)=$n
                AND ($n=0 OR ((SELECT min(ordinal) FROM ResultItems WHERE session_id=$id)=0 AND (SELECT max(ordinal) FROM ResultItems WHERE session_id=$id)=$n-1));
                """;
            finish.Parameters.AddWithValue("$n",count);finish.Parameters.AddWithValue("$pending",pending);finish.Parameters.AddWithValue("$failed",unresolvable);finish.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);finish.Parameters.AddWithValue("$id",id);
            if(finish.ExecuteNonQuery()!=1)throw new InvalidDataException("结果序列不连续。");
            stop.Token.ThrowIfCancellationRequested();finishTransaction.Commit();
            // A cancellation racing the Ready commit is still terminally Cancelled.
            stop.Token.ThrowIfCancellationRequested();
            activeSessionId=id;
            return new(id,filter.RootId,epoch,generation,count,pending,unresolvable){ConfirmedMatchCount=matched,IsPendingView=filter.IncludePending,CollectionId=filter.CollectionId};
        }
        catch(Exception ex)
        {
            // Detach interrupt before recording the terminal outcome.
            interrupt.Dispose();deadline.DisposeAsync().AsTask().GetAwaiter().GetResult();
            string code=cancellation.IsCancellationRequested?"Cancelled":resourceFault switch {1=>"Timeout",2=>"CatalogWalLimit",3=>"SessionDiskLimit",_=>ex is SqliteException {SqliteErrorCode:13}?"DiskFull":"SnapshotFailed"};
            if(started)Execute(session,"UPDATE ResultSessions SET state=$state,error_code=$error,completed_utc_ticks=$now WHERE session_id=$id",("$state",cancellation.IsCancellationRequested?"cancelled":"failed"),("$error",code),("$now",DateTime.UtcNow.Ticks),("$id",id));
            Exception Classified(Exception error){error.Data["FolderLens.SnapshotFailure"]=code;error.Data["FolderLens.CandidateId"]=id;return error;}
            if(cancellation.IsCancellationRequested)throw Classified(new OperationCanceledException(cancellation));
            if(resourceFault==1)throw Classified(new TimeoutException("结果快照超过硬期限；已保留之前的结果。",ex));
            if(resourceFault is 2 or 3)throw Classified(new IOException("结果快照触及 WAL 或会话磁盘预算；可重试。",ex));
            Classified(ex);
            throw;
        }
        finally { deadline.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private void PruneSessions(SqliteConnection session,int keep)
    {
        using var count=session.CreateCommand();count.CommandText="SELECT count(*) FROM ResultSessions WHERE active_leases>0 OR session_id=$active";count.Parameters.AddWithValue("$active",activeSessionId??"");
        long held=(long)count.ExecuteScalar()!;
        if(held>keep)throw new IOException("结果会话仍在使用，请先关闭历史结果。");
        Execute(session,"DELETE FROM ResultSessions WHERE active_leases=0 AND session_id<>$active AND session_id NOT IN (SELECT session_id FROM ResultSessions WHERE state='ready' AND active_leases=0 AND session_id<>$active ORDER BY created_utc_ticks DESC,session_id DESC LIMIT $keep)",("$active",activeSessionId??""),("$keep",keep-held));
        Checkpoint(session,sessionPath,true);
    }
    public Task<IReadOnlyList<SnapshotItem>> ReadPage(string sessionId,long ordinal,int count=256,CancellationToken cancellation=default)
    {
        if(ordinal<0 || count is <1 or >256)throw new ArgumentOutOfRangeException(nameof(ordinal));
        return sessionReader.Execute<IReadOnlyList<SnapshotItem>>(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT i.ordinal,i.entry_id,i.observed_version,i.snapshot_relative_path,i.directory_id,i.snapshot_logical_bytes,i.snapshot_allocated_bytes,i.snapshot_kind,g.group_id,g.relative_path,g.logical_bytes,g.match_count,g.start_ordinal,g.item_count,g.scan_state,g.capacity_scope,i.source_root_id,i.source_root_path,i.source_root_epoch FROM ResultItems i LEFT JOIN ResultGroups g ON g.session_id=i.session_id AND g.group_id=i.group_id WHERE i.session_id=$s AND i.ordinal >= $i AND EXISTS(SELECT 1 FROM ResultSessions WHERE session_id=$s AND state IN('building','ready')) ORDER BY i.ordinal LIMIT $n";
            cmd.Parameters.AddWithValue("$s",sessionId);cmd.Parameters.AddWithValue("$i",ordinal);cmd.Parameters.AddWithValue("$n",count);
            using var rows=cmd.ExecuteReader();var result=new List<SnapshotItem>(count);
            while(rows.Read())result.Add(new(rows.GetInt64(0),rows.GetString(1),rows.GetInt64(2),rows.GetString(3),rows.GetString(4),rows.GetInt64(5),rows.IsDBNull(6)?null:rows.GetInt64(6),rows.GetString(7)){Group=rows.IsDBNull(8)?null:ReadGroup(rows,8),SourceRootId=rows.IsDBNull(16)?null:rows.GetString(16),SourceRootPath=rows.IsDBNull(17)?null:rows.GetString(17),SourceRootEpoch=rows.IsDBNull(18)?null:rows.GetInt64(18)});
            return result;
        },cancellation);
    }
    private static SnapshotGroup ReadGroup(SqliteDataReader rows,int offset)=>new(rows.GetString(offset),rows.GetString(offset+1),rows.GetInt64(offset+2),rows.GetInt64(offset+3),rows.GetInt64(offset+4),rows.GetInt64(offset+5),rows.GetString(offset+6),rows.GetString(offset+7));
    public Task<IReadOnlyList<SnapshotGroup>> ReadGroups(string sessionId,CancellationToken cancellation=default)=>sessionReader.Execute<IReadOnlyList<SnapshotGroup>>(c=>
    {
        using var command=c.CreateCommand();command.CommandText="SELECT group_id,relative_path,logical_bytes,match_count,start_ordinal,item_count,scan_state,capacity_scope FROM ResultGroups WHERE session_id=$session ORDER BY start_ordinal";command.Parameters.AddWithValue("$session",sessionId);
        using var rows=command.ExecuteReader();var result=new List<SnapshotGroup>();long bytes=0;
        while(rows.Read())
        {
            cancellation.ThrowIfCancellationRequested();var group=ReadGroup(rows,0);bytes+=256+group.RelativePath.Length*2L+group.Id.Length*2L;
            if(bytes>32L<<20)throw new IOException("文件夹分组数量超过当前显示预算，请选择更小的根目录。");result.Add(group);
        }
        return result;
    },cancellation);
    public Task<T> Write<T>(Func<SqliteConnection,T> action,CancellationToken cancellation=default)=>writer.Execute(action,cancellation);
    public Task<bool> ApplyImageMetadata(string entryId,long expectedVersion,string rootId,long expectedEpoch,int width,int height,string format,bool isRaw,bool animated,string provider,CancellationToken cancellation=default,int pageCount=1)=>writer.Execute(c=>
    {
        if(width<=0 || height<=0)throw new ArgumentOutOfRangeException(nameof(width));
        if(pageCount<1)throw new ArgumentOutOfRangeException(nameof(pageCount));
        using var t=c.BeginTransaction();
        int changed=DirectoryIndexer.Execute(c,t,"""
        UPDATE Files SET kind=CASE WHEN $pages>1 AND $raw=0 AND $animated=0 THEN 'other' ELSE 'image' END,kind_confidence='verified',format_id=$format,is_raw=$raw,is_animated=$animated,page_count=$pages,
        display_width=$w,display_height=$h,long_edge=max($w,$h),short_edge=min($w,$h),pixel_count=$pixels,source_metadata_version=$version
        WHERE entry_id=$entry AND file_version=$version AND root_id=$root AND entry_state='present' AND EXISTS(SELECT 1 FROM Roots WHERE root_id=$root AND root_epoch=$epoch);
        """,("$pages",animated?1:pageCount),("$format",format),("$raw",isRaw?1:0),("$animated",animated?1:0),("$w",width),("$h",height),("$pixels",checked((long)width*height)),("$version",expectedVersion),("$entry",entryId),("$root",rootId),("$epoch",expectedEpoch));
        if(changed==1)
        {
            foreach(string group in new[]{"identity","imageGeometry","animation"})DirectoryIndexer.Execute(c,t,"INSERT INTO FieldStates(entry_id,field_group,source_version,state,provider_version,attempt_count) VALUES($entry,$group,$version,'ready',$provider,1) ON CONFLICT(entry_id,field_group) DO UPDATE SET source_version=excluded.source_version,state='ready',provider_version=excluded.provider_version,attempt_count=FieldStates.attempt_count+1,error_code=NULL",("$entry",entryId),("$group",group),("$version",expectedVersion),("$provider",provider));
            DirectoryIndexer.Execute(c,t,"UPDATE SchemaInfo SET catalog_revision=catalog_revision+1");
        }
        t.Commit();return changed==1;
    },cancellation);
    public Task<bool> ApplyMediaMetadata(string entryId,long expectedVersion,string rootId,long expectedEpoch,MediaMetadata metadata,string provider,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        if(metadata.DurationMs<0 || metadata.Width<=0 || metadata.Height<=0 || (metadata.Width is null)!=(metadata.Height is null) || metadata.FrameRate is {} rate&&(!double.IsFinite(rate)||rate<=0) || metadata.FrameRateNumerator<=0 || metadata.FrameRateDenominator<=0 || (metadata.FrameRateNumerator is null)!=(metadata.FrameRateDenominator is null))throw new ArgumentException("媒体元数据无效。",nameof(metadata));
        long? numerator=metadata.FrameRateNumerator,denominator=metadata.FrameRateDenominator;
        if(numerator is null && metadata.FrameRate is {} fps){numerator=checked((long)Math.Round(fps*1_000_000));denominator=1_000_000;}
        string kind=metadata.VideoCodec is not null||metadata.VideoStream is not null?"video":"audio";
        long? width=kind=="video"?metadata.Width:null,height=kind=="video"?metadata.Height:null;
        using var t=c.BeginTransaction();
        int changed=DirectoryIndexer.Execute(c,t,"""
            UPDATE Files SET kind=$kind,kind_confidence='verified',format_id=$format,is_raw=NULL,is_animated=NULL,
                duration_ms=$duration,video_codec=$video,audio_codec=$audio,fps_num=$num,fps_den=$den,
                display_width=$w,display_height=$h,long_edge=$long,short_edge=$short,pixel_count=$pixels,source_metadata_version=$version
            WHERE entry_id=$entry AND file_version=$version AND root_id=$root AND entry_state='present'
                AND EXISTS(SELECT 1 FROM Roots WHERE root_id=$root AND root_epoch=$epoch);
            """,("$kind",kind),("$format",metadata.FormatId??(object)DBNull.Value),("$duration",metadata.DurationMs??(object)DBNull.Value),("$video",metadata.VideoCodec??(object)DBNull.Value),("$audio",metadata.AudioCodec??(object)DBNull.Value),("$num",numerator??(object)DBNull.Value),("$den",denominator??(object)DBNull.Value),
            ("$w",width??(object)DBNull.Value),("$h",height??(object)DBNull.Value),("$long",width is null?DBNull.Value:Math.Max(width.Value,height!.Value)),("$short",width is null?DBNull.Value:Math.Min(width.Value,height!.Value)),("$pixels",width is null?DBNull.Value:checked(width.Value*height!.Value)),("$version",expectedVersion),("$entry",entryId),("$root",rootId),("$epoch",expectedEpoch));
        if(changed==1)
        {
            foreach(string group in new[]{"identity","media","imageGeometry"})DirectoryIndexer.Execute(c,t,"INSERT INTO FieldStates(entry_id,field_group,source_version,state,provider_version,attempt_count) VALUES($entry,$group,$version,'ready',$provider,1) ON CONFLICT(entry_id,field_group) DO UPDATE SET source_version=excluded.source_version,state='ready',provider_version=excluded.provider_version,attempt_count=FieldStates.attempt_count+1,error_code=NULL,retry_after_utc_ticks=NULL",("$entry",entryId),("$group",group),("$version",expectedVersion),("$provider",provider));
            DirectoryIndexer.Execute(c,t,"UPDATE SchemaInfo SET catalog_revision=catalog_revision+1");
        }
        t.Commit();return changed==1;
    },cancellation);

    public Task<bool> MarkMetadataFailure(string entryId,long expectedVersion,string rootId,long expectedEpoch,string[] groups,string errorCode,bool unsupported,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        if(groups.Any(g=>g is not("identity" or "imageGeometry" or "animation" or "imageColor" or "captureTime" or "media" or "allocation")))throw new ArgumentException("元数据字段组无效。");
        using var t=c.BeginTransaction();int changed=0;
        foreach(string group in groups)changed+=DirectoryIndexer.Execute(c,t,"""
            INSERT INTO FieldStates(entry_id,field_group,source_version,state,error_code,attempt_count,retry_after_utc_ticks)
            SELECT entry_id,$group,$version,$state,$error,1,$retry FROM Files
            WHERE entry_id=$entry AND file_version=$version AND root_id=$root AND entry_state='present' AND EXISTS(SELECT 1 FROM Roots WHERE root_id=$root AND root_epoch=$epoch)
            ON CONFLICT(entry_id,field_group) DO UPDATE SET source_version=excluded.source_version,state=excluded.state,error_code=excluded.error_code,attempt_count=FieldStates.attempt_count+1,retry_after_utc_ticks=excluded.retry_after_utc_ticks;
            """,("$entry",entryId),("$group",group),("$version",expectedVersion),("$root",rootId),("$epoch",expectedEpoch),("$state",unsupported?"unsupported":"failed"),("$error",errorCode),("$retry",unsupported?DBNull.Value:DateTime.UtcNow.AddMinutes(1).Ticks));
        if(changed>0)DirectoryIndexer.Execute(c,t,"UPDATE SchemaInfo SET catalog_revision=catalog_revision+1");t.Commit();return changed>0;
    },cancellation);
    public Task<T> Read<T>(Func<SqliteConnection,T> action,CancellationToken cancellation=default)=>reader.Execute(action,cancellation);
    public async ValueTask DisposeAsync(){await writer.DisposeAsync();await reader.DisposeAsync();await interactiveReader.DisposeAsync();await sessionReader.DisposeAsync();}
}
