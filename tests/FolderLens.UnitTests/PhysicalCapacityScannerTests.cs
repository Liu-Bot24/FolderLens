using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class PhysicalCapacityScannerTests
{
    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>
    {public void Report(T value)=>report(value);}

    [Fact]
    public async Task CancellationStopsPhysicalTraversal()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-capacity-stop-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for(int i=0;i<300;i++)File.WriteAllBytes(Path.Combine(root,$"item-{i:D4}.txt"),[1]);
            using var stop=new CancellationTokenSource();
            var progress=new InlineProgress<CapacityReport>(report=>{if(report.State=="scanning")stop.Cancel();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>PhysicalCapacityScanner.Scan(root,[],progress,stop.Token));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task CountsWholeFolderWithoutBrowseCatalogAndHonorsSkipScan()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-capacity-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"A","AA"));Directory.CreateDirectory(Path.Combine(root,"B"));
        try
        {
            File.WriteAllBytes(Path.Combine(root,"root.txt"),new byte[10]);
            File.WriteAllBytes(Path.Combine(root,"A","a.txt"),new byte[20]);
            File.WriteAllBytes(Path.Combine(root,"A","AA","deep.txt"),new byte[30]);
            File.WriteAllBytes(Path.Combine(root,"B","b.txt"),new byte[40]);
            var report=await PhysicalCapacityScanner.Scan(root,[],null,CancellationToken.None);
            Assert.True(report.IsComplete);Assert.Equal(100,report.Rows.Single(r=>r.RelativePath=="").SubtreeLogical);
            Assert.Equal(50,report.Rows.Single(r=>r.RelativePath=="A").SubtreeLogical);
            Assert.Equal(4,report.Rows.Single(r=>r.RelativePath=="").SubtreeFiles);
            var excluded=await PhysicalCapacityScanner.Scan(root,[new ExclusionSpec("B","skipScan")],null,CancellationToken.None);
            Assert.True(excluded.IsComplete);Assert.Equal(60,excluded.Rows.Single(r=>r.RelativePath=="").SubtreeLogical);
            Assert.DoesNotContain(excluded.Rows,r=>r.RelativePath=="B");
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void RuntimeCatalogHasNoFixedTwoGibPageLimit()
    {
        using var c=new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");c.Open();
        using(var q=c.CreateCommand())
        {
            q.CommandText="CREATE TABLE Files(root_id TEXT,entry_state TEXT,name_sort_key BLOB,logical_bytes INTEGER,mtime_utc_ticks INTEGER,long_edge INTEGER,pixel_count INTEGER,duration_ms INTEGER)";q.ExecuteNonQuery();
        }
        CompactBrowsingCatalog.Configure(c,maximumBytes:null);
        using var command=c.CreateCommand();command.CommandText="PRAGMA max_page_count";
        Assert.True((long)command.ExecuteScalar()! *4096 > CompactBrowsingCatalog.MaximumBytes);
    }

    [Fact]
    public async Task BrowsingSessionOpensWithoutFixedTwoGibLimit()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-budget-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var session=await BrowsingSessionStorage.Open(directory);
            long bytes=await session.Catalog.Write(c=>
            {
                using var command=c.CreateCommand();command.CommandText="PRAGMA max_page_count";
                long pages=(long)command.ExecuteScalar()!;command.CommandText="PRAGMA page_size";
                return checked(pages*(long)command.ExecuteScalar()!);
            });
            Assert.True(bytes>CompactBrowsingCatalog.MaximumBytes);
            await session.Catalog.EnsureBrowsingBudget(CancellationToken.None);
        }
        finally{Directory.Delete(directory,true);}
    }
}
