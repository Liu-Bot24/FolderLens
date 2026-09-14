using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CollectionSelectionPerformanceTests
{
    [Fact]
    public async Task SparseSelectionUsesOrdinalIndexAndNotFullSessionScan()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using var catalog=new CatalogStore(data);await catalog.Initialize();await catalog.SeedBenchmark(100);
        var snapshot=await catalog.CreateSnapshot(new(){RootId="benchmark"},1,1);await catalog.RetainSnapshot(snapshot.Id);
        try
        {
            var plan=await catalog.Write(c=>
            {
                using var cmd=c.CreateCommand();cmd.CommandText="ATTACH DATABASE $path AS collection_selection";cmd.Parameters.AddWithValue("$path",Path.Combine(data,"sessions.sqlite"));cmd.ExecuteNonQuery();
                try
                {
                    cmd.CommandText="EXPLAIN QUERY PLAN SELECT f.location_key "+CatalogStore.CollectionSelectionRowsSql;cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$session",snapshot.Id);cmd.Parameters.AddWithValue("$ranges","[{\"Start\":1,\"Count\":1},{\"Start\":70,\"Count\":1}]");
                    using var rows=cmd.ExecuteReader();var lines=new List<string>();while(rows.Read())lines.Add(rows.GetString(3));return lines;
                }
                finally{cmd.CommandText="DETACH DATABASE collection_selection";cmd.ExecuteNonQuery();}
            });
            Assert.Contains(plan,line=>line.Contains("session_id=? AND ordinal>? AND ordinal<?"));
            string tag=(await catalog.CreateCollection("sparse")).Id;
            Assert.Equal(2,await catalog.ChangeCollectionSelection([tag],snapshot.Id,[new(1,1),new(70,1)],true));
            Assert.Equal(98,(await catalog.CreateSnapshot(new(){RootId="benchmark",ExcludeCollections=[tag]},1,2)).Count);
        }
        finally{await catalog.ReleaseSnapshot(snapshot.Id);}
    }
}
