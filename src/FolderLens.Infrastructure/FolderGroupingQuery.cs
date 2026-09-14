using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

// Connection-local, disk-backed directory plan. No file objects or ancestor strings are materialized.
internal sealed class FolderGroupingQuery : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private bool disposed;
    public const string Join=" JOIN temp.lens_folder_groups fg ON fg.id=f.directory_id JOIN temp.lens_folder_groups owner ON owner.id=fg.group_id ";
    public const string OrderPrefix="owner.preorder,";
    public FolderGroupingQuery(SqliteConnection connection,SqliteTransaction transaction,FilterSpec filter,CancellationToken cancellation)
    {
        this.connection=connection;this.transaction=transaction;
        try{Build(filter,cancellation);}catch{Dispose();throw;}
    }
    private void Build(FilterSpec filter,CancellationToken cancellation)
    {
        var options=filter.Grouping;options.Validate();
        string directory=filter.DirectoryScope.Replace('/','\\');
        int baseDepth=directory.Length==0?0:directory.Count(c=>c=='\\')+1;
        connection.CreateFunction("lens_natural",(string value)=>NaturalOrder.Key(value),true);
        Execute("PRAGMA temp.cache_size=-8192; PRAGMA temp.max_page_count=65536;");
        Execute("""
            DROP TABLE IF EXISTS temp.lens_folder_groups;
            CREATE TEMP TABLE lens_folder_groups(
                id TEXT PRIMARY KEY,parent TEXT,path TEXT NOT NULL,depth INTEGER NOT NULL,name_key BLOB NOT NULL,
                direct_bytes INTEGER NOT NULL DEFAULT 0,bytes INTEGER NOT NULL DEFAULT 0,
                direct_matches INTEGER NOT NULL DEFAULT 0,matches INTEGER NOT NULL DEFAULT 0,
                nodes INTEGER NOT NULL DEFAULT 1,preorder INTEGER,group_id TEXT
            ) STRICT;
            CREATE INDEX temp.ix_lens_group_depth ON lens_folder_groups(depth);
            CREATE INDEX temp.ix_lens_group_parent ON lens_folder_groups(parent);
            INSERT INTO lens_folder_groups(id,parent,path,depth,name_key)
            SELECT directory_id,CASE WHEN relative_path=$scope THEN NULL ELSE parent_id END,relative_path,
                CASE WHEN relative_path='' THEN 0 ELSE length(relative_path)-length(replace(relative_path,'\',''))+1 END-$depth,
                lens_natural(name) FROM Directories WHERE root_id=$root
                AND ($scope='' OR relative_path=$scope OR substr(relative_path,1,length($scope)+1)=$prefix);
            """,new Dictionary<string,object>{{"$root",filter.RootId},{"$scope",directory},{"$prefix",directory+"\\"},{"$depth",baseDepth}});
        if(directory.Length>0&&Scalar("SELECT count(*) FROM lens_folder_groups")==0)return;
        if(Scalar("SELECT count(*) FROM lens_folder_groups WHERE depth=0 AND parent IS NULL")!=1||
           Scalar("SELECT count(*) FROM lens_folder_groups child LEFT JOIN lens_folder_groups p ON p.id=child.parent WHERE child.depth>0 AND (p.id IS NULL OR p.depth<>child.depth-1)")!=0)
            throw new InvalidDataException("目录层级尚不完整，请完成扫描后刷新分组。");

        var matches=FilterSql.Build(filter);
        var scope=options.CapacityScope=="matches"?matches:FilterSql.Build(new FilterSpec
        {
            RootId=filter.RootId,ObservedRootEpoch=filter.ObservedRootEpoch,DirectoryScope=filter.DirectoryScope,Kinds=[],ShowHidden=true,Exclusions=filter.Exclusions.Where(rule=>rule.Mode=="skipScan").ToArray()
        });
        Aggregate("direct_bytes","sum(f.logical_bytes)",scope);
        Aggregate("direct_matches","count(*)",matches);
        Execute("UPDATE lens_folder_groups SET bytes=direct_bytes,matches=direct_matches");
        long maxDepth=Scalar("SELECT coalesce(max(depth),0) FROM lens_folder_groups");
        for(long depth=maxDepth;depth>0;depth--)
        {
            cancellation.ThrowIfCancellationRequested();
            Execute("""
                WITH totals AS MATERIALIZED (
                    SELECT parent,sum(bytes) AS bytes,sum(matches) AS matches,sum(nodes) AS nodes
                    FROM lens_folder_groups WHERE depth=$depth GROUP BY parent
                )
                UPDATE lens_folder_groups AS p SET bytes=p.bytes+t.bytes,matches=p.matches+t.matches,nodes=p.nodes+t.nodes
                FROM totals t WHERE p.id=t.parent;
                """,new Dictionary<string,object>{{"$depth",depth}});
        }
        Execute("UPDATE lens_folder_groups SET preorder=0,group_id=id WHERE depth=0");
        string column=options.Field switch{"name"=>"name_key","matchCount"=>"matches",_=>"bytes"};
        string order=$"{column} {options.Direction.ToUpperInvariant()},name_key,path,id";
        for(long depth=1;depth<=maxDepth;depth++)
        {
            cancellation.ThrowIfCancellationRequested();
            string owner=options.Levels=="all"||depth==1?"child.id":"p.group_id";
            Execute($"""
                WITH siblings AS MATERIALIZED (
                    SELECT id,parent,coalesce(sum(nodes) OVER(PARTITION BY parent ORDER BY {order}
                        ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) AS preceding_nodes
                    FROM lens_folder_groups WHERE depth=$depth
                )
                UPDATE lens_folder_groups AS child SET preorder=p.preorder+1+s.preceding_nodes,group_id={owner}
                FROM siblings s JOIN lens_folder_groups p ON p.id=s.parent WHERE child.id=s.id;
                """,new Dictionary<string,object>{{"$depth",depth}});
        }
        cancellation.ThrowIfCancellationRequested();
    }
    private void Aggregate(string target,string expression,FilterQuery query)
    {
        Execute($"""
            WITH totals AS MATERIALIZED (
                SELECT f.directory_id,{expression} AS value FROM Files f WHERE {query.MatchExpression} GROUP BY f.directory_id
            )
            UPDATE lens_folder_groups AS g SET {target}=t.value FROM totals t WHERE g.id=t.directory_id;
            """,query.Parameters);
    }
    private void Execute(string sql,IReadOnlyDictionary<string,object>? parameters=null)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction.Connection is null?null:transaction;command.CommandText=sql;
        if(parameters is not null)foreach(var value in parameters)command.Parameters.AddWithValue(value.Key,value.Value);
        command.ExecuteNonQuery();
    }
    private long Scalar(string sql)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;return (long)command.ExecuteScalar()!;
    }
    public void Dispose(){if(disposed)return;Execute("DROP TABLE IF EXISTS temp.lens_folder_groups;");disposed=true;}
}
