using FolderLens.Core;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

internal static class FileObservationWriter
{
    private const string changed="(Files.stat_signature<>excluded.stat_signature OR Files.entry_state<>excluded.entry_state OR $force=1)";
    private static readonly string[] metadata=["format_id","is_raw","is_animated","display_width","display_height","long_edge","short_edge","pixel_count","encoded_width","encoded_height","orientation","bit_depth","frame_count","page_count","duration_ms","fps_num","fps_den","video_codec","audio_codec","capture_wall_ticks","capture_offset_minutes","capture_utc_ticks","source_metadata_version"];
    private static readonly string reset=string.Join(",",metadata.Select(field=>$"{field}=CASE WHEN {changed} THEN NULL ELSE Files.{field} END"));
    private static readonly string Upsert=$"""
            INSERT INTO Files(entry_id,root_id,directory_id,name,extension,relative_path,canonical_key,path_sort_key,name_sort_key,natural_key_version,stat_signature,kind,kind_confidence,logical_bytes,allocated_bytes,physical_identity,mtime_utc_ticks,ctime_utc_ticks,file_attributes,hydration_state,entry_state,last_seen_scan_id,updated_revision,file_version,observed_revision)
            VALUES($id,$root,$dir,$name,$ext,$path,$path,$pathkey,$namekey,1,$signature,$kind,'extension',$bytes,$allocated,$physical,$modified,$created,$attributes,$hydration,$state,$scan,0,$initialVersion,$observed)
            ON CONFLICT(entry_id) DO UPDATE SET observed_revision=excluded.observed_revision,logical_bytes=excluded.logical_bytes,allocated_bytes=excluded.allocated_bytes,physical_identity=excluded.physical_identity,mtime_utc_ticks=excluded.mtime_utc_ticks,ctime_utc_ticks=excluded.ctime_utc_ticks,file_attributes=excluded.file_attributes,hydration_state=excluded.hydration_state,last_seen_scan_id=excluded.last_seen_scan_id,entry_state=excluded.entry_state,
            file_version=Files.file_version+CASE WHEN {changed} THEN 1 ELSE 0 END,
            kind=CASE WHEN {changed} THEN excluded.kind ELSE Files.kind END,kind_confidence=CASE WHEN {changed} THEN 'extension' ELSE Files.kind_confidence END,
            {reset},stat_signature=excluded.stat_signature;
            """;
    internal static long NextRevision(SqliteConnection c,SqliteTransaction transaction)
    {
        DirectoryIndexer.Execute(c,transaction,"UPDATE SchemaInfo SET catalog_revision=catalog_revision+1");
        using var command=c.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT catalog_revision FROM SchemaInfo";return (long)command.ExecuteScalar()!;
    }
    internal static string Signature(ScanEntry item)=>$"{item.Bytes}:{item.Modified}:{item.Created}:{item.ChangeTime}:{item.PhysicalIdentity}:{item.Attributes}";
    internal static void Write(DirectoryIndexer.BatchCommands commands,string id,string root,string directoryId,string path,string? scan,ScanEntry item,long initialVersion,bool forceRefresh,long observedRevision)
    {
            string extension=Path.GetExtension(item.Name).ToLowerInvariant(),kind=FileKinds.Candidate(item.Name);
            string signature=Signature(item);
            commands.Execute(Upsert,("$id",id),("$root",root),("$dir",directoryId),("$name",item.Name),("$ext",extension),("$path",path),("$pathkey",NaturalOrder.Key(path)),("$namekey",NaturalOrder.Key(item.Name)),("$signature",signature),("$kind",kind),("$bytes",item.Bytes),("$allocated",item.Allocated),("$physical",item.PhysicalIdentity),("$modified",item.Modified),("$created",item.Created),("$attributes",item.Attributes),("$hydration",item.Hydration),("$state",item.SkipReason is null?"present":"excluded"),("$scan",scan),("$force",forceRefresh?1:0),("$initialVersion",initialVersion),("$observed",observedRevision));
            commands.Execute("DELETE FROM FieldStates WHERE entry_id=$id AND source_version<>(SELECT file_version FROM Files WHERE entry_id=$id)",("$id",id));
            if(item.Hydration=="placeholder")
                foreach(string group in new[]{"identity","imageGeometry","animation","imageColor","captureTime","media","allocation"})
                    commands.Execute("INSERT INTO FieldStates(entry_id,field_group,source_version,state,error_code) SELECT entry_id,$group,file_version,'deferredOffline','LocalOnly' FROM Files WHERE entry_id=$id ON CONFLICT(entry_id,field_group) DO UPDATE SET source_version=excluded.source_version,state='deferredOffline',error_code='LocalOnly'",("$id",id),("$group",group));
    }
}
