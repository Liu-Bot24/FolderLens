using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DetailedPropertiesTests
{
    [Fact]
    public async Task CaptureTimezoneWallClockAndMissingStayDistinctThroughSqlAndProperties()
    {
        var known=CaptureTimeInfo.FromExif("2026:09:12 12:34:56","+08:00","123456789");
        Assert.Equal(new DateTime(2026,9,12,4,34,56).Ticks+1234567,known.UtcTicks);Assert.Equal(480,known.OffsetMinutes);
        var wall=CaptureTimeInfo.FromExif("2026:09:12 12:34:56",null);Assert.Null(wall.UtcTicks);Assert.True(wall.TimezoneUnknown);
        Assert.Equal("failed",CaptureTimeInfo.FromExif("2026:99:12 12:34:56","+08:00").State);
        Assert.Equal("InvalidCaptureOffset",CaptureTimeInfo.FromExif("2026:09:12 12:34:56","+14:30").ErrorCode);
        string path=Path.Combine(Path.GetTempPath(),"FolderLens-details",Guid.NewGuid().ToString("N"));
        await using var catalog=new CatalogStore(path);await catalog.Initialize();await catalog.SeedBenchmark(3);
        var details=new ContentMetadataDetails{State="ready",Capture=known,CameraMake="Fixture",CameraModel="Camera",Iso=400,ExposureSeconds=1.0/125,ExposureRational="1/125",EncodedWidth=640,EncodedHeight=480,BitDepth=8,FrameCount=1,PageCount=1,ColorState="ready",HasIccProfile=false};
        Assert.True(await catalog.ApplyFileDetails("000000000001",1,"benchmark",1,details,"fixture-v1"));
        Assert.True(await catalog.ApplyFileDetails("000000000002",1,"benchmark",1,details with{Capture=wall},"fixture-v1"));
        Assert.True(await catalog.ApplyFileDetails("000000000003",1,"benchmark",1,details with{Capture=CaptureTimeInfo.Missing()},"fixture-v1"));
        Assert.False(await catalog.ApplyFileDetails("000000000001",2,"benchmark",1,details,"fixture-v1"));
        var utc=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Dates=[new("captured","utc","2026-09-12T00:00:00Z","2026-09-13T00:00:00Z")]},1,1);Assert.Equal(1,utc.Count);Assert.Equal(2,utc.Unresolvable);Assert.Equal(0,utc.Pending);
        var local=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Dates=[new("captured","captureWall","2026-09-12T00:00:00","2026-09-13T00:00:00")]},1,2);Assert.Equal(2,local.Count);Assert.Equal(1,local.Unresolvable);
        var props=await catalog.ReadFileProperties("benchmark","000000000001",1);Assert.NotNull(props);Assert.Equal("Camera",props.Details!.CameraModel);Assert.Equal(400,props.Details.Iso);Assert.Equal(640,props.EncodedWidth);Assert.Equal("ready",props.FieldStates["captureTime"].State);Assert.Null(await catalog.ReadFileProperties("benchmark","000000000001",2));
    }
    [Fact]
    public async Task CatalogV1MigrationPreservesExistingRowsWithoutFullCopy()
    {
        string path=Path.Combine(Path.GetTempPath(),"FolderLens-details",Guid.NewGuid().ToString("N"));
        await using(var legacy=new CatalogStore(path))
        {
            await legacy.Initialize();await legacy.SeedBenchmark(1);
            await legacy.Write(c=>{using var cmd=c.CreateCommand();LegacyCollectionSchema.Catalog(c);cmd.CommandText="DROP TABLE FileDetails; UPDATE SchemaInfo SET schema_version=1; PRAGMA user_version=1;";return cmd.ExecuteNonQuery();});
        }
        await using(var updated=new CatalogStore(path)){await updated.Initialize();Assert.NotNull(await updated.ReadFileProperties("benchmark","000000000001",1));}
        Assert.Empty(Directory.GetFiles(path,"catalog.sqlite.pre-*.bak"));
        using var verify=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(path,"catalog.sqlite"),Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly}.ToString());verify.Open();using var cmd=verify.CreateCommand();cmd.CommandText="PRAGMA user_version";Assert.Equal(8L,cmd.ExecuteScalar());cmd.CommandText="SELECT count(*) FROM Files";Assert.Equal(1L,cmd.ExecuteScalar());
    }
    [Fact]
    public async Task RealSonyArwExifComesFromReadOnlyWorkerProbe()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;Assert.NotNull(project);
        string input=Path.Combine(project.FullName,"fixtures","public","sony-a7r4a-14bit.ARW"),exe=Path.Combine(project.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        var before=new FileInfo(input);long length=before.Length,modified=before.LastWriteTimeUtc.Ticks;
        await using var worker=new WorkerClient(exe,Path.Combine(Path.GetTempPath(),"FolderLens-details",Guid.NewGuid().ToString("N")));
        var reply=await worker.Request(input,"probe",new("fixture",1,1,1,1,1),new(),CancellationToken.None);
        var details=reply.Message.Metadata!.Value.GetProperty("details").Deserialize<ContentMetadataDetails>(WorkerProtocol.Json)!;
        Assert.Equal("ready",details.State);Assert.Contains("SONY",details.CameraMake!,StringComparison.OrdinalIgnoreCase);Assert.Contains("ILCE-7RM4A",details.CameraModel!);Assert.NotNull(details.Capture.WallTicks);Assert.True(details.Iso>0);Assert.True(details.ExposureSeconds>0);Assert.True(details.EncodedWidth>=9000);Assert.True(details.EncodedHeight>=6000);
        string evidence=Path.Combine(project.FullName,"artifacts","m0","properties");Directory.CreateDirectory(evidence);await File.WriteAllTextAsync(Path.Combine(evidence,"sony-a7r4a-details.json"),JsonSerializer.Serialize(details,new JsonSerializerOptions(WorkerProtocol.Json){WriteIndented=true}));
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-details",Guid.NewGuid().ToString("N")));await catalog.Initialize();await catalog.SeedBenchmark(1);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET name=$name,relative_path=$name,logical_bytes=$length,mtime_utc_ticks=$modified,stat_signature=$signature";cmd.Parameters.AddWithValue("$name",Path.GetFileName(input));cmd.Parameters.AddWithValue("$length",length);cmd.Parameters.AddWithValue("$modified",modified);cmd.Parameters.AddWithValue("$signature",FileReadObservation.Read(input).Signature);return cmd.ExecuteNonQuery();});
        await new MetadataPump(catalog,worker,scanExecutable:Path.Combine(project.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe")).FillGeometry("benchmark",Path.GetDirectoryName(input)!,1,null,CancellationToken.None);
        var actual=await catalog.ReadFileProperties("benchmark","000000000001",1);Assert.Equal(details.Capture.UtcTicks,actual!.CaptureUtcTicks);Assert.Equal(details.CameraModel,actual.Details!.CameraModel);
        string start=new DateTime(details.Capture.UtcTicks!.Value,DateTimeKind.Utc).Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",System.Globalization.CultureInfo.InvariantCulture),end=new DateTime(details.Capture.UtcTicks.Value,DateTimeKind.Utc).Date.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",System.Globalization.CultureInfo.InvariantCulture);
        var filtered=await catalog.CreateSnapshot(new FilterSpec{RootId="benchmark",Raw="only",Dates=[new("captured","utc",start,end)]},1,1);Assert.Equal(1,filtered.Count);
        before.Refresh();Assert.Equal(length,before.Length);Assert.Equal(modified,before.LastWriteTimeUtc.Ticks);
    }
}
