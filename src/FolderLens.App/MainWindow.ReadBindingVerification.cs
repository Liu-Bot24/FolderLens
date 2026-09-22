using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyReadBinding(string source,Dictionary<string,object> report)
    {
        await OpenRoot(Path.Combine(source,"A"));if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;
        // This NTFS fixture deliberately substitutes an ID-less scan record.
        // Stop automatic re-enumeration before the substitution: the real NTFS
        // scanner would legitimately replace it with a newer complete version.
        scanStop.Cancel();monitor?.Dispose();monitor=null;
        if(scanTask is not null)try{await scanTask;}catch(OperationCanceledException){}
        var item=(await catalog!.ReadFirstPage(new(){RootId=rootId})).Items.First();
        var file=(await catalog.ReadFileProperties(rootId,item.EntryId,item.Version))!;
        string[] head=file.SourceSignature.Split(':',5);
        string partial=string.Join(':',head.Take(4))+":"+head[4][head[4].LastIndexOf(':')..];
        await catalog.Write(c=>
        {
            using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET stat_signature=$signature,physical_identity=NULL WHERE entry_id=$entry";
            cmd.Parameters.AddWithValue("$signature",partial);cmd.Parameters.AddWithValue("$entry",item.EntryId);return cmd.ExecuteNonQuery();
        });
        var row=new FileRow(item.Ordinal);row.Fill(item);row.UpdateProperties((await catalog.ReadFileProperties(rootId,item.EntryId,item.Version))!);
        try
        {
            var unexpected=await previewWorker!.Request(SourcePath(row),"thumbnail",Context(row,selection),new(256,256),lifetime.Token,Stamp(row));
            await previewWorker.ReleaseAsset(unexpected);throw new InvalidOperationException("Incomplete scan observation unexpectedly passed strict decoding.");
        }
        catch(Exception error)when(error.Message.Contains("FileChanged",StringComparison.Ordinal)){report["strictDecoderRejectedIncompleteObservation"]=true;}
        FileProperties bound;
        try{bound=await ResolveRow(row,rootId,lifetime.Token);}
        catch(Exception error)
        {
            report["bindingFailureData"]=error.Data;
            report["latestBindingRow"]=await catalog.Read(c=>
            {
                using var cmd=c.CreateCommand();cmd.CommandText="SELECT json_object('version',f.file_version,'state',f.entry_state,'signature',f.stat_signature,'epoch',r.root_epoch,'locationState',l.state,'scanState',r.scan_state) FROM Files f JOIN Roots r ON r.root_id=f.root_id JOIN DirectoryLocationBindings b ON b.directory_id=f.directory_id JOIN DirectoryLocations l ON l.location_id=b.location_id WHERE f.entry_id=$entry";
                cmd.Parameters.AddWithValue("$entry",item.EntryId);return cmd.ExecuteScalar()?.ToString()??"missing";
            });throw;
        }
        if(!bound.ReadObservationBound||row.SourceSignature!=file.SourceSignature)throw new InvalidOperationException("Read binding did not restore the full source observation.");
        foreach(string operation in new[]{"thumbnail","fit"})
        {
            var reply=await previewWorker!.Request(SourcePath(row),operation,Context(row,selection),new(256,256),lifetime.Token,Stamp(row));
            try
            {
                if(reply.AssetPath is null)throw new InvalidOperationException("Decoder returned no image.");
                using var bitmap=await LoadLocalBitmap(reply.AssetPath);
                if(bitmap.SizeInPixels.Width==0||bitmap.SizeInPixels.Height==0||bitmap.GetPixelBytes().Length==0)throw new InvalidOperationException("Decoded image has no pixels.");
                report[operation+"AfterBinding"]=true;
            }
            finally{await previewWorker.ReleaseAsset(reply);}
        }
        report["status"]="PASS";
    }
}
