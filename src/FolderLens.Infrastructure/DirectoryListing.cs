using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

/// <summary>Immutable, disk-backed sibling list. Page reads never materialize the whole list.</summary>
public sealed class DirectoryListing : IAsyncDisposable,IDisposable
{
    public const int PageSize=128;
    private readonly string file;
    private readonly DirectoryListingLease lease;
    private readonly object gate=new();
    private bool disposed;
    public long Count {get;}
    public string Version=>Path.GetFileNameWithoutExtension(file);
    private DirectoryListing(DirectoryListingLease lease,long count){this.lease=lease;file=lease.FilePath;Count=count;}
    public Task<IReadOnlyList<string>> ReadPage(long start,CancellationToken cancellation=default)=>Task.Run<IReadOnlyList<string>>(()=>
    {
        if(start<0||start>Count)throw new ArgumentOutOfRangeException(nameof(start));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);cancellation.ThrowIfCancellationRequested();
            using var db=Open(file,true);using var command=db.CreateCommand();
            command.CommandText="SELECT path FROM Rows WHERE ordinal >= $start ORDER BY ordinal LIMIT 128";command.Parameters.AddWithValue("$start",start);
            using var reader=command.ExecuteReader();var paths=new List<string>(PageSize);
            while(reader.Read()){cancellation.ThrowIfCancellationRequested();paths.Add(reader.GetString(0));}return paths;
        }
    },cancellation);
    public Task<long?> Find(string path,CancellationToken cancellation=default)=>Task.Run<long?>(()=>
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);cancellation.ThrowIfCancellationRequested();using var db=Open(file,true);using var command=db.CreateCommand();
            command.CommandText="SELECT ordinal FROM Rows WHERE path=$path";command.Parameters.AddWithValue("$path",path);return command.ExecuteScalar() is long index?index:null;
        }
    },cancellation);
    public void Dispose(){lock(gate){disposed=true;lease.Delete();}}
    public ValueTask DisposeAsync()=>new(Task.Run(Dispose));
    private static SqliteConnection Open(string file,bool readOnly=false)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate}.ToString());
        try{db.Open();return db;}catch{db.Dispose();throw;}
    }
    internal static long MeasureStorage(string directory,Action<string>? beforeLength=null)
    {
        long bytes=0;
        foreach(var path in Directory.EnumerateFiles(directory))
        {
            beforeLength?.Invoke(path);
            try{bytes+=new FileInfo(path).Length;}
            catch(FileNotFoundException){}
            catch(DirectoryNotFoundException){}
        }
        return bytes;
    }
    internal sealed class Builder : IDisposable
    {
        private readonly string directory,file;
        private readonly DirectoryListingLease lease;
        private readonly SqliteConnection db;
        private readonly SqliteTransaction transaction;
        private readonly SqliteCommand insert;
        private readonly CancellationToken cancellation;
        private bool published;
        private long count;
        internal Builder(string directory,CancellationToken cancellation)
        {
            this.directory=directory;this.cancellation=cancellation;cancellation.ThrowIfCancellationRequested();
            lease=DirectoryListingLease.Create(directory);file=lease.FilePath;
            try
            {
                CheckBudget();
                db=Open(file);
                using(var setup=db.CreateCommand()){setup.CommandText="PRAGMA page_size=4096; PRAGMA max_page_count=32768; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE; CREATE TABLE Pending(path TEXT PRIMARY KEY COLLATE BINARY) WITHOUT ROWID;";setup.ExecuteNonQuery();}
                transaction=db.BeginTransaction();insert=db.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO Pending(path) VALUES($path)";insert.Parameters.Add("$path",SqliteType.Text);
            }
            catch{db?.Dispose();lease.Delete();throw;}
        }
        internal void Add(string path)
        {
            cancellation.ThrowIfCancellationRequested();if(count%128==0)CheckBudget();insert.Parameters[0].Value=path;insert.ExecuteNonQuery();count++;
        }
        internal DirectoryListing Complete()
        {
            cancellation.ThrowIfCancellationRequested();using var command=db.CreateCommand();command.Transaction=transaction;
            command.CommandText="CREATE TABLE Rows(ordinal INTEGER PRIMARY KEY,path TEXT UNIQUE COLLATE BINARY NOT NULL); INSERT INTO Rows SELECT ROW_NUMBER() OVER(ORDER BY path COLLATE BINARY)-1,path FROM Pending; DROP TABLE Pending;";
            using var interrupt=cancellation.Register(()=>SQLitePCL.raw.sqlite3_interrupt(db.Handle));command.ExecuteNonQuery();cancellation.ThrowIfCancellationRequested();transaction.Commit();CheckBudget();published=true;return new(lease,count);
        }
        private void CheckBudget()
        {
            cancellation.ThrowIfCancellationRequested();if(MeasureStorage(directory)>512L*1024*1024)throw new IOException("目录列表暂存空间已达到 512 MiB，保留原列表；请关闭部分目录后重试。");
        }
        public void Dispose(){insert.Dispose();transaction.Dispose();db.Dispose();if(!published)lease.Delete();}
    }
    internal static DirectoryListing Create(string directory,IEnumerable<string> paths,CancellationToken cancellation)
    {
        using var builder=new Builder(directory,cancellation);foreach(var path in paths)builder.Add(path);return builder.Complete();
    }
}
