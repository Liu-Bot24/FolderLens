using System.Diagnostics;
using FolderLens.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace FolderLens.UnitTests;

public sealed class StartupMigrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task UpgradeProfilePreservesExistingIsolatedFixture()
    {
        string? selected=Environment.GetEnvironmentVariable("FOLDERLENS_UPGRADE_FIXTURE");
        string data=selected is null?Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")):Path.GetFullPath(selected);
        if(selected is not null&&!data.EndsWith("FolderLens\\artifacts\\startup-profile-real-catalog\\catalog",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Only the existing isolated migration fixture is permitted.");
        long before;
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();if(selected is null)await c.SeedBenchmark(10000);
            before=await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files";return (long)cmd.ExecuteScalar()!;});
            var prepare=Stopwatch.StartNew();await c.Write(db=>{LegacyCollectionSchema.V4(db);return true;});
            output.WriteLine($"Restored v4 fixture in {prepare.Elapsed.TotalMilliseconds:F1} ms");
        }
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var timer=Stopwatch.StartNew();CatalogStore.OperationMigrationMeasured=(phase,ms)=>output.WriteLine($"{phase}: {ms:F1} ms");
        try
        {
            await using var c=new CatalogStore(data);await c.Initialize(stop.Token);
            output.WriteLine($"Full upgrade: {timer.Elapsed.TotalMilliseconds:F1} ms, {before} files");
            Assert.Equal(before,await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files WHERE length(location_key)=67 AND location_key LIKE 'v5:%'";return (long)cmd.ExecuteScalar()!;}));
            Assert.Empty(Directory.GetFiles(data,"catalog.sqlite.pre-*.bak"));
        }
        finally{CatalogStore.OperationMigrationMeasured=null;}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringKeyConversionRollsBackAndRetryKeepsCollections(bool afterPositionMigration)
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();await c.SeedBenchmark(10000);
            string tag=(await c.CreateCollection("rollback")).Id;await c.ChangeCollectionMembers([tag],["000000000001"],true);
            await c.Write(db=>{LegacyCollectionSchema.V4(db);return true;});
        }
        using var cancel=new CancellationTokenSource();
        await using(var c=new CatalogStore(data))
        {
            CatalogStore.OperationMigrationMeasured=(phase,ms)=>{if(afterPositionMigration&&phase=="directoryLocationPrepared")cancel.Cancel();};
            try{await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>c.Initialize(cancel.Token,p=>{if(!afterPositionMigration&&p.Contains(" / "))cancel.Cancel();}));}
            finally{CatalogStore.OperationMigrationMeasured=null;}
            Assert.Equal(4,await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";return (long)cmd.ExecuteScalar()!;}));
            Assert.Equal(10000,await c.Read(db=>{using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM Files WHERE location_key NOT LIKE 'v5:%'";return (long)cmd.ExecuteScalar()!;}));
            Assert.Equal(1,(await c.ReadCollections()).Single().Count);
        }
        await using(var c=new CatalogStore(data)){await c.Initialize();Assert.Equal(1,(await c.ReadCollections()).Single().Count);}
        Assert.Empty(Directory.GetFiles(data,"*.bak"));
    }
    [Fact]
    public async Task V4ConversionKeepsDistinctOriginalMemberAnchors()
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();await c.SeedBenchmark(2);
            await c.Write(db=>{using var cmd=db.CreateCommand();cmd.CommandText="""
                INSERT INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES('second','benchmark','second','second','second','sensitive');
                INSERT INTO ScanDirectoryIdentities VALUES('benchmark-dir','same-parent'),('second','same-parent');
                UPDATE Files SET physical_identity='same-file',name='same.jpg',relative_path='same.jpg' WHERE entry_id='000000000001';
                UPDATE Files SET directory_id='second',physical_identity='same-file',name='same.jpg',relative_path='second\same.jpg' WHERE entry_id='000000000002';
                """;return cmd.ExecuteNonQuery();});
            string tag=(await c.CreateCollection("separate positions")).Id;
            await c.ChangeCollectionMembers([tag],["000000000001","000000000002"],true);
            await c.Write(db=>
            {
                LegacyCollectionSchema.V4(db);
                using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM CollectionMembers;INSERT INTO CollectionMembers SELECT $tag,location_key,entry_id,0 FROM Files";cmd.Parameters.AddWithValue("$tag",tag);return cmd.ExecuteNonQuery();
            });
            Assert.Equal(2,(await c.ReadCollections()).Single().Count);
        }
        await using(var c=new CatalogStore(data)){await c.Initialize();Assert.Equal(2,(await c.ReadCollections()).Single().Count);}
    }
    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task V4UpgradePreservesTagsAndReportsProgressWithoutFullCopies(int count)
    {
        string data=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string tag;
        await using(var c=new CatalogStore(data))
        {
            await c.Initialize();await c.SeedBenchmark(count);
            tag=(await c.CreateCollection("preserve during upgrade")).Id;
            await c.ChangeCollectionMembers([tag],["000000000001"],true);
            await c.Write(db=>{LegacyCollectionSchema.V4(db);return true;});
        }
        var timer=Stopwatch.StartNew();
        CatalogStore.OperationMigrationMeasured=(phase,ms)=>output.WriteLine($"{phase}: {ms:F1} ms");
        try
        {
            var progress=new List<string>();
            await using var c=new CatalogStore(data);await c.Initialize(progress:progress.Add);
            output.WriteLine($"{count} files: {timer.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Equal(1,(await c.ReadCollections()).Single().Count);
            Assert.Single((await c.ReadFirstPage(new(){RootId="benchmark",IncludeCollections=[tag]})).Items);
            Assert.Empty(Directory.GetFiles(data,"catalog.sqlite.pre-*.bak"));
            Assert.Contains(progress,p=>p.Contains($"{count:N0} / {count:N0}"));
        }
        finally { CatalogStore.OperationMigrationMeasured=null; }
    }
}
