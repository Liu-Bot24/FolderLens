using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class SnapshotKindTests
{
    [Fact]
    public async Task VersionTwoUpgradePreservesUngroupedReadySessionWithoutFullCopy()
    {
        string directory=Temp();ResultHandle handle;
        await using(var catalog=new CatalogStore(directory))
        {await catalog.Initialize();await catalog.SeedBenchmark(2);handle=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);}
        using(var connection=Open(Path.Combine(directory,"sessions.sqlite")))
        {using var command=connection.CreateCommand();LegacyCollectionSchema.Sessions(connection);command.CommandText="DROP TABLE ResultGroups; ALTER TABLE ResultItems DROP COLUMN group_id; PRAGMA user_version=2;";command.ExecuteNonQuery();}
        await using(var reopened=new CatalogStore(directory))
        {
            await reopened.Initialize();var items=await reopened.ReadPage(handle.Id,0);Assert.Equal(2,items.Count);Assert.All(items,item=>Assert.Null(item.Group));Assert.Empty(await reopened.ReadGroups(handle.Id));
        }
        Assert.Empty(Directory.GetFiles(directory,"sessions.sqlite.pre-*.bak"));
    }
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-snapshot-kind",Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FirstPageAndSnapshotCaptureAllKindsAndKeepThemAcrossReopen()
    {
        string directory=Temp();string[] kinds=["image","video","audio","text","markdown","other"];ResultHandle handle;
        await using(var catalog=new CatalogStore(directory))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(kinds.Length);
            await catalog.Write(c=>{for(int i=0;i<kinds.Length;i++){using var command=c.CreateCommand();command.CommandText="UPDATE Files SET kind=$kind WHERE entry_id=$id";command.Parameters.AddWithValue("$kind",kinds[i]);command.Parameters.AddWithValue("$id",(i+1).ToString("D12"));command.ExecuteNonQuery();}return true;});
            var filter=new FilterSpec{RootId="benchmark",Kinds=[]};
            Assert.Equal(kinds,(await catalog.ReadFirstPage(filter)).Items.Select(item=>item.Kind));
            handle=await catalog.CreateSnapshot(filter,1,1);
            await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET kind='other'";return command.ExecuteNonQuery();});
            Assert.Equal(kinds,(await catalog.ReadPage(handle.Id,0)).Select(item=>item.Kind));
        }
        await using var reopened=new CatalogStore(directory);await reopened.Initialize();
        Assert.Equal(kinds,(await reopened.ReadPage(handle.Id,0)).Select(item=>item.Kind));
        Assert.Empty(Directory.GetFiles(directory,"sessions.sqlite.pre-v2-*.bak"));
    }

    [Fact]
    public async Task UpgradesLegacySessionWithoutFullCopyAndRequiresRebuildInsteadOfGuessingKinds()
    {
        string directory=Temp();ResultHandle legacy;
        await using(var catalog=new CatalogStore(directory))
        {
            await catalog.Initialize();await catalog.SeedBenchmark(2);
            legacy=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,1);
        }
        using(var connection=Open(Path.Combine(directory,"sessions.sqlite")))
        {
            using var command=connection.CreateCommand();
            LegacyCollectionSchema.Sessions(connection);command.CommandText="DROP TABLE ResultGroups; ALTER TABLE ResultItems DROP COLUMN group_id; ALTER TABLE ResultItems DROP COLUMN snapshot_kind; PRAGMA user_version=1;";command.ExecuteNonQuery();
        }
        await using(var upgraded=new CatalogStore(directory))
        {
            await upgraded.Initialize();
            Assert.Empty(await upgraded.ReadPage(legacy.Id,0));Assert.False(await upgraded.RetainSnapshot(legacy.Id));
            var rebuilt=await upgraded.CreateSnapshot(new FilterSpec{RootId="benchmark"},1,2);
            Assert.Equal(2,rebuilt.Count);Assert.All(await upgraded.ReadPage(rebuilt.Id,0),item=>Assert.Equal("image",item.Kind));
        }
        Assert.Empty(Directory.GetFiles(directory,"sessions.sqlite.pre-*.bak"));
        using var current=Open(Path.Combine(directory,"sessions.sqlite"));using var version=current.CreateCommand();
        version.CommandText="PRAGMA user_version";Assert.Equal(5L,version.ExecuteScalar());
    }

    private static SqliteConnection Open(string path)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());connection.Open();return connection;
    }
}
