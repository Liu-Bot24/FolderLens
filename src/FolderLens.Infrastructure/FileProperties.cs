using System.Text.Json;
using FolderLens.Contracts;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed record MetadataFieldState(string State,string? ErrorCode,string? Provider,long Attempts);
public sealed record FileProperties
{
    public required string EntryId {get;init;}
    public required string RootId {get;init;}
    public required string RelativePath {get;init;}
    public required string Name {get;init;}
    public required string Kind {get;init;}
    public required string EntryState {get;init;}
    public long Version {get;init;}
    public string SourceSignature {get;init;}="";
    public bool ReadObservationBound {get;init;}
    public bool IsCollected {get;init;}
    public long LogicalBytes {get;init;}
    public long? AllocatedBytes {get;init;}
    public long ModifiedUtcTicks {get;init;}
    public long? CreatedUtcTicks {get;init;}
    public long? CaptureUtcTicks {get;init;}
    public long? CaptureWallTicks {get;init;}
    public int? CaptureOffsetMinutes {get;init;}
    public bool CaptureTimezoneUnknown=>CaptureWallTicks is not null&&CaptureUtcTicks is null;
    public string? Format {get;init;}
    public bool? Raw {get;init;}
    public bool? Animated {get;init;}
    public long? Width {get;init;}
    public long? Height {get;init;}
    public long? EncodedWidth {get;init;}
    public long? EncodedHeight {get;init;}
    public long? PixelCount {get;init;}
    public long? BitDepth {get;init;}
    public long? FrameCount {get;init;}
    public long? PageCount {get;init;}
    public long? DurationMs {get;init;}
    public string? VideoCodec {get;init;}
    public string? AudioCodec {get;init;}
    public long? FrameRateNumerator {get;init;}
    public long? FrameRateDenominator {get;init;}
    public string? HydrationState {get;init;}
    public ContentMetadataDetails? Details {get;init;}
    public IReadOnlyDictionary<string,MetadataFieldState> FieldStates {get;init;}=new Dictionary<string,MetadataFieldState>();
}

public sealed partial class CatalogStore
{
    public Task<FileProperties?> ReadFileProperties(string rootId,string entryId,long version,CancellationToken cancellation=default)=>interactiveReader.Execute<FileProperties?>(c=>
    {
        using var transaction=c.BeginTransaction(deferred:true);using var cmd=c.CreateCommand();cmd.Transaction=transaction;
        cmd.CommandText="SELECT f.*,d.detail_json,EXISTS(SELECT 1 FROM CollectionMembers m WHERE m.location_key=f.location_key AND m.directory_location_id=(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=f.directory_id)) AS is_collected FROM Files f LEFT JOIN FileDetails d ON d.entry_id=f.entry_id AND d.source_version=f.file_version WHERE f.root_id=$root AND f.entry_id=$entry AND f.file_version=$version";
        cmd.CommandText=cmd.CommandText.Replace("SELECT f.*", "SELECT f.*,coalesce(rb.source_signature,f.stat_signature) AS read_signature,coalesce(rb.hydration_state,f.hydration_state) AS read_hydration,rb.entry_id IS NOT NULL AS read_bound")
            .Replace("LEFT JOIN FileDetails",ReadBindingJoin+" LEFT JOIN FileDetails");
        cmd.Parameters.AddWithValue("$root",rootId);cmd.Parameters.AddWithValue("$entry",entryId);cmd.Parameters.AddWithValue("$version",version);
        FileProperties file;
        using(var row=cmd.ExecuteReader())
        {
            if(!row.Read())return null;
            string? Text(string field){int i=row.GetOrdinal(field);return row.IsDBNull(i)?null:row.GetString(i);}
            long? Number(string field){int i=row.GetOrdinal(field);return row.IsDBNull(i)?null:row.GetInt64(i);}
            string? json=Text("detail_json");ContentMetadataDetails? details=json is null?null:JsonSerializer.Deserialize<ContentMetadataDetails>(json,WorkerProtocol.Json);
            file=new(){SourceSignature=Text("read_signature")!,ReadObservationBound=Number("read_bound")==1,EntryId=entryId,RootId=rootId,Version=version,RelativePath=Text("relative_path")!,Name=Text("name")!,Kind=Text("kind")!,EntryState=Text("entry_state")!,LogicalBytes=Number("logical_bytes")!.Value,AllocatedBytes=Number("allocated_bytes"),ModifiedUtcTicks=Number("mtime_utc_ticks")!.Value,CreatedUtcTicks=Number("ctime_utc_ticks"),CaptureUtcTicks=Number("capture_utc_ticks"),CaptureWallTicks=Number("capture_wall_ticks"),CaptureOffsetMinutes=(int?)Number("capture_offset_minutes"),Format=Text("format_id"),Raw=Number("is_raw") is {} raw?raw!=0:null,Animated=Number("is_animated") is {} animated?animated!=0:null,Width=Number("display_width"),Height=Number("display_height"),EncodedWidth=Number("encoded_width"),EncodedHeight=Number("encoded_height"),PixelCount=Number("pixel_count"),BitDepth=Number("bit_depth"),FrameCount=Number("frame_count"),PageCount=Number("page_count"),DurationMs=Number("duration_ms"),VideoCodec=Text("video_codec"),AudioCodec=Text("audio_codec"),FrameRateNumerator=Number("fps_num"),FrameRateDenominator=Number("fps_den"),HydrationState=Text("read_hydration"),Details=details,IsCollected=Number("is_collected")==1};
        }
        using var groups=c.CreateCommand();groups.Transaction=transaction;groups.CommandText="SELECT field_group,state,error_code,provider_version,attempt_count FROM FieldStates WHERE entry_id=$entry AND source_version=$version";groups.Parameters.AddWithValue("$entry",entryId);groups.Parameters.AddWithValue("$version",version);
        var states=new Dictionary<string,MetadataFieldState>();using(var rows=groups.ExecuteReader())while(rows.Read())states.Add(rows.GetString(0),new(rows.GetString(1),rows.IsDBNull(2)?null:rows.GetString(2),rows.IsDBNull(3)?null:rows.GetString(3),rows.GetInt64(4)));
        transaction.Commit();return file with{FieldStates=states};
    },cancellation);

    public Task<bool> ApplyFileDetails(string entryId,long expectedVersion,string rootId,long expectedEpoch,ContentMetadataDetails details,string provider,CancellationToken cancellation=default)=>writer.Execute(c=>
    {
        provider=details.Provider??provider;
        ValidateDetails(details);string json=JsonSerializer.Serialize(details,WorkerProtocol.Json);if(System.Text.Encoding.UTF8.GetByteCount(json)>65536)throw new InvalidDataException("媒体属性超过存储预算。");
        using var t=c.BeginTransaction();
        int changed=DirectoryIndexer.Execute(c,t,"""
            UPDATE Files SET capture_utc_ticks=$utc,capture_wall_ticks=$wall,capture_offset_minutes=$offset,
                encoded_width=$width,encoded_height=$height,orientation=$orientation,bit_depth=$depth,frame_count=$frames,page_count=$pages
            WHERE entry_id=$entry AND file_version=$version AND root_id=$root AND entry_state='present' AND EXISTS(SELECT 1 FROM Roots WHERE root_id=$root AND root_epoch=$epoch);
            """,("$utc",details.Capture.UtcTicks??(object)DBNull.Value),("$wall",details.Capture.WallTicks??(object)DBNull.Value),("$offset",details.Capture.OffsetMinutes??(object)DBNull.Value),
            ("$width",details.EncodedWidth??(object)DBNull.Value),("$height",details.EncodedHeight??(object)DBNull.Value),("$orientation",details.ExifOrientation??(object)DBNull.Value),("$depth",details.BitDepth??(object)DBNull.Value),("$frames",details.FrameCount??(object)DBNull.Value),("$pages",details.PageCount??(object)DBNull.Value),("$entry",entryId),("$version",expectedVersion),("$root",rootId),("$epoch",expectedEpoch));
        if(changed==1)
        {
            DirectoryIndexer.Execute(c,t,"INSERT INTO FileDetails(entry_id,source_version,provider_version,detail_json,updated_utc_ticks) VALUES($entry,$version,$provider,$json,$now) ON CONFLICT(entry_id) DO UPDATE SET source_version=excluded.source_version,provider_version=excluded.provider_version,detail_json=excluded.detail_json,updated_utc_ticks=excluded.updated_utc_ticks",("$entry",entryId),("$version",expectedVersion),("$provider",provider),("$json",json),("$now",DateTime.UtcNow.Ticks));
            WriteDetailState(c,t,entryId,expectedVersion,"captureTime",details.Capture.State,details.Capture.ErrorCode,provider);
            WriteDetailState(c,t,entryId,expectedVersion,"imageColor",details.ColorState,details.ColorErrorCode,provider);
            DirectoryIndexer.Execute(c,t,"UPDATE SchemaInfo SET catalog_revision=catalog_revision+1");
        }
        t.Commit();return changed==1;
    },cancellation);
    private static void WriteDetailState(SqliteConnection c,SqliteTransaction t,string entry,long version,string group,string state,string? error,string provider)
    {
        DirectoryIndexer.Execute(c,t,"INSERT INTO FieldStates(entry_id,field_group,source_version,state,error_code,provider_version,attempt_count,retry_after_utc_ticks) VALUES($entry,$group,$version,$state,$error,$provider,1,$retry) ON CONFLICT(entry_id,field_group) DO UPDATE SET source_version=excluded.source_version,state=excluded.state,error_code=excluded.error_code,provider_version=excluded.provider_version,attempt_count=FieldStates.attempt_count+1,retry_after_utc_ticks=excluded.retry_after_utc_ticks",
            ("$entry",entry),("$group",group),("$version",version),("$state",state),("$error",error??(object)DBNull.Value),("$provider",provider),("$retry",state=="failed"?DateTime.UtcNow.AddMinutes(1).Ticks:DBNull.Value));
    }
    private static void ValidateDetails(ContentMetadataDetails details)
    {
        if(details.SchemaVersion!=1||details.State is not("ready" or "failed" or "unsupported" or "notRequested")||details.ColorState is not("ready" or "failed" or "unsupported" or "notRequested")||details.Capture.State is not("ready" or "failed" or "unsupported" or "notRequested")||details.Capture.OffsetMinutes is <-840 or >840||details.Capture.UtcTicks<0||details.Capture.WallTicks<0||details.Capture.UtcTicks>DateTime.MaxValue.Ticks||details.Capture.WallTicks>DateTime.MaxValue.Ticks||details.EncodedWidth<=0||details.EncodedHeight<=0||details.ExifOrientation is <1 or >8||details.BitDepth is <1 or >128||details.FrameCount<1||details.PageCount<1||details.Streams.Length>16)throw new InvalidDataException("媒体属性无效。");
        if(details.Capture.UtcTicks is {} utc)
        {
            if(details.Capture.WallTicks is not {} wall||details.Capture.OffsetMinutes is not {} offset||checked(wall-(long)offset*TimeSpan.TicksPerMinute)!=utc||details.Capture.TimezoneUnknown)throw new InvalidDataException("拍摄时间的墙钟与 UTC 不一致。");
        }
        foreach(double? value in new[]{details.ExposureSeconds,details.Aperture,details.ExposureBias,details.FocalLengthMm,details.RotationDegrees})if(value is {} number&&!double.IsFinite(number))throw new InvalidDataException("媒体数值必须有限。");
    }
}
