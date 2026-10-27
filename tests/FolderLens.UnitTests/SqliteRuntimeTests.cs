using System.Runtime.CompilerServices;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class SqliteRuntimeTests
{
    // As in the app, Infrastructure configures SQLite before anything opens it.
#pragma warning disable CA2255
    [ModuleInitializer] internal static void ConfigureBeforeAnyConnection()=>SqliteRuntime.EnsureConfigured();
#pragma warning restore CA2255

    [Fact]
    public async Task StatementJournalThresholdAppliesAndCatalogWritesStillWork()
    {
        Assert.True(SqliteRuntime.StatementJournalConfigured);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-sqlite-runtime",Guid.NewGuid().ToString("N"));
        await using var catalog=new CatalogStore(directory);await catalog.Initialize();await catalog.SeedBenchmark(500);
        Assert.Equal(500,(await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Kinds=[]},1,1)).Count);
    }
}
