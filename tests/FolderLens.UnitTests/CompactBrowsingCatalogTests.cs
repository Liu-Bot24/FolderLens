using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class CompactBrowsingCatalogTests
{
    [Fact]
    public void CompactIndexesPreserveTieOrderingAndReduceLongPathStorage()
    {
        using var c=new SqliteConnection("Data Source=:memory:");c.Open();using var q=c.CreateCommand();
        q.CommandText="CREATE TABLE Files(root_id TEXT,entry_state TEXT,name_sort_key BLOB,logical_bytes INTEGER,mtime_utc_ticks INTEGER,long_edge INTEGER,pixel_count INTEGER,duration_ms INTEGER,path_sort_key BLOB,entry_id TEXT PRIMARY KEY)";q.ExecuteNonQuery();
        var keys=new[]{("Name","name_sort_key"),("Size","logical_bytes"),("Mtime","mtime_utc_ticks"),("LongEdge","long_edge"),("Pixels","pixel_count"),("Duration","duration_ms")};
        foreach(var (name,column) in keys){q.CommandText=$"CREATE INDEX IX_Files_{name} ON Files(root_id,entry_state,{column},path_sort_key,entry_id)";q.ExecuteNonQuery();}
        using(var t=c.BeginTransaction())
        {
            q.Transaction=t;q.CommandText="INSERT INTO Files VALUES('r','present',$name,$size,1,NULL,NULL,NULL,$path,$id)";
            var name=q.Parameters.Add("$name",SqliteType.Blob);var size=q.Parameters.Add("$size",SqliteType.Integer);var path=q.Parameters.Add("$path",SqliteType.Blob);var id=q.Parameters.Add("$id",SqliteType.Text);
            for(int i=0;i<500;i++){name.Value=NaturalOrder.Key($"photo{i%10}");size.Value=i%7;path.Value=NaturalOrder.Key(new string('路',160)+$"\\{500-i}.jpg");id.Value=i.ToString();q.ExecuteNonQuery();}t.Commit();q.Transaction=null;q.Parameters.Clear();
        }
        string[] Read(){q.CommandText="SELECT entry_id FROM Files WHERE root_id='r' AND entry_state='present' ORDER BY logical_bytes,path_sort_key,entry_id";using var r=q.ExecuteReader();var rows=new List<string>();while(r.Read())rows.Add(r.GetString(0));return rows.ToArray();}
        long Bytes(){q.CommandText="SELECT (p.page_count-f.freelist_count)*s.page_size FROM pragma_page_count p,pragma_freelist_count f,pragma_page_size s";return (long)q.ExecuteScalar()!;}
        var before=Read();long wide=Bytes();CompactBrowsingCatalog.Configure(c);long compact=Bytes();Assert.Equal(before,Read());Assert.True(compact<wide*0.6,$"wide={wide}, compact={compact}");
        q.CommandText="PRAGMA max_page_count";Assert.Equal(CompactBrowsingCatalog.MaximumBytes/4096,(long)q.ExecuteScalar()!);
    }
    [Theory]
    [InlineData(@"D:\app\data\runtime\catalog.sqlite",true)]
    [InlineData(@"D:\app\database\photo.jpg",false)]
    public void OwnDataChangesAreIgnoredWithoutIgnoringSimilarFolderNames(string path,bool expected)=>Assert.Equal(expected,RootChangeMonitor.IsIgnoredPath(path,@"D:\app\data"));
}
