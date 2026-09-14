using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FolderLens.Diagnostics;

internal static class MigrationCheck
{
    internal static int Run(string directory,string previous)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"catalog.sqlite"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();
        using var command=connection.CreateCommand();command.CommandText="ATTACH DATABASE $path AS prior";command.Parameters.AddWithValue("$path",previous);command.ExecuteNonQuery();command.Parameters.Clear();
        long Count(string sql){command.CommandText=sql;return (long)command.ExecuteScalar()!;}
        long version=Count("PRAGMA user_version"),oldFiles=Count("SELECT count(*) FROM prior.Files"),files=Count("SELECT count(*) FROM Files");
        long oldCollections=Count("SELECT count(*) FROM prior.Collections"),collections=Count("SELECT count(*) FROM Collections");
        command.CommandText="PRAGMA quick_check";string integrity=(string)command.ExecuteScalar()!;
        command.CommandText="PRAGMA foreign_key_check";using(var rows=command.ExecuteReader()){if(rows.Read())throw new InvalidDataException("Migrated catalog has a foreign-key violation.");}
        long wrongKeys=Count("SELECT count(*) FROM Files WHERE length(location_key)<>67 OR substr(location_key,1,3)<>'v5:'");
        long mismatches=Count("""
            WITH expected AS (
                SELECT m.collection_id,coalesce(f.location_key,m.location_key) location_key
                FROM prior.CollectionMembers m LEFT JOIN prior.Files old ON old.entry_id=m.entry_id
                LEFT JOIN Files f ON f.entry_id=m.entry_id WHERE old.entry_state IS NOT 'missing'
            ), missing AS (SELECT * FROM expected EXCEPT SELECT collection_id,location_key FROM CollectionMembers),
            extra AS (SELECT collection_id,location_key FROM CollectionMembers EXCEPT SELECT * FROM expected)
            SELECT (SELECT count(*) FROM missing)+(SELECT count(*) FROM extra)
            """);
        bool pass=version==6&&oldFiles==files&&oldCollections==collections&&integrity=="ok"&&wrongKeys==0&&mismatches==0;
        var result=new{status=pass?"PASS":"FAIL",version,oldFiles,files,oldCollections,collections,integrity,wrongKeys,membershipMismatches=mismatches};
        string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"migration-check.json"),json);Console.WriteLine(json);return pass?0:1;
    }
}
