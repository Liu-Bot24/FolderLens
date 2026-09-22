using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

public sealed partial class CatalogStore
{
    // Optional, bounded-by-demand observation cache. Old binaries may ignore
    // this table; validity depends on the exact scan/version/location tuple.
    private const string ReadBindingSchema="""
        CREATE TABLE IF NOT EXISTS FileReadBindings(
          entry_id TEXT PRIMARY KEY REFERENCES Files(entry_id) ON DELETE CASCADE,
          source_version INTEGER NOT NULL,scan_signature TEXT NOT NULL,path_revision INTEGER NOT NULL,
          root_epoch INTEGER NOT NULL,location_id TEXT NOT NULL,binding_revision INTEGER NOT NULL,
          source_signature TEXT NOT NULL,hydration_state TEXT NOT NULL) STRICT;
        """;
    private const string ReadBindingJoin="""
        LEFT JOIN FileReadBindings rb ON rb.entry_id=f.entry_id AND rb.source_version=f.file_version
          AND rb.scan_signature=f.stat_signature AND rb.path_revision=f.path_revision
          AND rb.root_epoch=(SELECT root_epoch FROM Roots WHERE root_id=f.root_id)
          AND (rb.location_id,rb.binding_revision)=(SELECT location_id,binding_revision FROM DirectoryLocationBindings WHERE directory_id=f.directory_id)
        """;
    private sealed record ReadBindingOwner(long Epoch,string Root,string Relative,string Signature,long PathRevision,string Location,long Binding,string? ParentIdentity,string? ParentAnchor);
    private static ReadBindingOwner? ReadBindingPosition(SqliteConnection c,SqliteTransaction t,string rootId,string entryId,long version)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="""
            SELECT r.root_epoch,r.display_path,f.relative_path,f.stat_signature,f.path_revision,b.location_id,b.binding_revision,l.directory_identity,l.anchor_locator
            FROM Files f JOIN Roots r ON r.root_id=f.root_id JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id
            WHERE f.root_id=$root AND f.entry_id=$entry AND f.file_version=$version AND f.entry_state='present' AND l.state='active'
            """;
        cmd.Parameters.AddWithValue("$root",rootId);cmd.Parameters.AddWithValue("$entry",entryId);cmd.Parameters.AddWithValue("$version",version);
        using var row=cmd.ExecuteReader();return row.Read()?new(row.GetInt64(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetInt64(4),row.GetString(5),row.GetInt64(6),row.IsDBNull(7)?null:row.GetString(7),row.IsDBNull(8)?null:row.GetString(8)):null;
    }
    internal Func<string,bool,SourceFileStamp?,CancellationToken,Task<ScanDirectoryPacket>>? ReadBindingProbeOverride {get;set;}
    public async Task<FileProperties> ResolveFileRead(string rootId,string entryId,long version,SourceFileProbe probe,bool allowCloud,CancellationToken token)
    {
        var file=await ReadFileProperties(rootId,entryId,version,token).ConfigureAwait(false)??throw new IOException("FileChanged");
        if(file.HydrationState=="placeholder"&&!allowCloud)return file;
        if(file.HydrationState!="placeholder"&&SourceObservationSignature.Parse(file.SourceSignature).Identity.Length>0)return file;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));var cancellation=deadline.Token;
        var owner=await interactiveReader.Execute(c=>{using var t=c.BeginTransaction(deferred:true);return ReadBindingPosition(c,t,rootId,entryId,version);},cancellation).ConfigureAwait(false)??throw new IOException("FileChanged");
        if(owner.Signature!=file.SourceSignature)throw new IOException("FileChanged");
        string root=Path.GetFullPath(owner.Root).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        string path=FolderLens.Core.PathRules.ValidateSource(Path.GetFullPath(Path.Combine(root,owner.Relative)));
        if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("FileChanged");
        string parent=Path.GetDirectoryName(path)!;
        Task<ScanDirectoryPacket> Read(string target,SourceFileStamp? prepare=null)=>ReadBindingProbeOverride is {} test
            ?test(target,allowCloud,prepare,cancellation):probe.ReadPacket(target,cancellation,allowCloud,prepare,prepare is null?null:new(owner.ParentIdentity!,owner.ParentAnchor!));
        bool ParentMatches(ScanDirectoryPacket packet)=>packet.State=="present"&&packet.FileStamp is null&&owner.ParentIdentity is not null&&owner.ParentAnchor is not null
            &&owner.ParentIdentity==packet.PhysicalIdentity&&owner.ParentAnchor==packet.ResolvedLocation;
        var parentBefore=await Read(parent).ConfigureAwait(false);if(!ParentMatches(parentBefore))throw new IOException("FileChanged");
        var observed=await Read(path,new(file.LogicalBytes,file.ModifiedUtcTicks,file.SourceSignature)).ConfigureAwait(false);
        if(observed.State!="present"||observed.FileObservation is not {} full||full.SkipReason is not null)throw new IOException(observed.ErrorCode??"SourceIoError");
        var after=await Read(path).ConfigureAwait(false);var parentAfter=await Read(parent).ConfigureAwait(false);
        string signature=FileObservationWriter.Signature(full);
        if(string.IsNullOrEmpty(full.PhysicalIdentity))throw new IOException("IdentityUnavailable");
        if(after.State!="present"||after.FileObservation is not {} last||signature!=FileObservationWriter.Signature(last)||!ParentMatches(parentAfter)
            ||parentBefore.VolumeIdentity!=parentAfter.VolumeIdentity||parentBefore.CaseMode!=parentAfter.CaseMode)throw new IOException("FileChanged");
        bool contentChanged=await writer.Execute(c=>
        {
            CompactBrowsingCatalog.EnsureWriteHeadroom(c,64L<<10);
            using var t=c.BeginTransaction();if(ReadBindingPosition(c,t,rootId,entryId,version)!=owner)throw new IOException("FileChanged");
            // A concurrent resolver must not replace the first accepted identity.
            using var previous=c.CreateCommand();previous.Transaction=t;previous.CommandText="SELECT source_signature FROM FileReadBindings WHERE entry_id=$entry AND source_version=$version AND scan_signature=$scan AND root_epoch=$epoch AND location_id=$location AND binding_revision=$binding AND path_revision=$path";
            foreach(var pair in new (string,object)[]{("$entry",entryId),("$version",version),("$scan",owner.Signature),("$epoch",owner.Epoch),("$location",owner.Location),("$binding",owner.Binding),("$path",owner.PathRevision)})previous.Parameters.AddWithValue(pair.Item1,pair.Item2);
            if(previous.ExecuteScalar() is string accepted&&accepted!=signature)throw new IOException("FileChanged");
            // Request ownership may expire while content identity remains useful.
            // Compare prior content even across epochs/location revisions.
            using var continuity=c.CreateCommand();continuity.Transaction=t;
            continuity.CommandText="SELECT source_signature FROM FileReadBindings WHERE entry_id=$entry AND source_version=$version";
            continuity.Parameters.AddWithValue("$entry",entryId);continuity.Parameters.AddWithValue("$version",version);
            bool changed=continuity.ExecuteScalar() is string oldSignature&&oldSignature!=signature;
            if(changed)FileObservationWriter.InvalidateContent(c,t,entryId,file.Name);
            long boundVersion=changed?checked(version+1):version;
            DirectoryIndexer.Execute(c,t,"INSERT OR REPLACE INTO FileReadBindings VALUES($entry,$version,$scan,$path,$epoch,$location,$binding,$signature,$hydration)",
                ("$entry",entryId),("$version",boundVersion),("$scan",owner.Signature),("$path",owner.PathRevision),("$epoch",owner.Epoch),("$location",owner.Location),("$binding",owner.Binding),("$signature",signature),("$hydration",full.Hydration));
            DirectoryIndexer.Execute(c,t,"DELETE FROM FieldStates WHERE entry_id=$entry AND source_version=$version AND error_code='FileChanged'",("$entry",entryId),("$version",version));
            t.Commit();return changed;
        },cancellation).ConfigureAwait(false);
        if(contentChanged)throw new IOException("FileChanged");
        return await ReadFileProperties(rootId,entryId,version,cancellation).ConfigureAwait(false)??throw new IOException("FileChanged");
    }
}
