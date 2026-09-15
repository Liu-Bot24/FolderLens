using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task ReadDemandedMetadata(FileRow row,CancellationToken token,bool readDetails=false,WorkerClient? decoder=null)
    {
        if(catalog is null||metadataWorker is null||media is null||row.Item is null||row.Kind is not ("image" or "video" or "audio")||row.HydrationState=="placeholder")return;
        await new MetadataPump(catalog,decoder??(readDetails?previewWorker:null)??metadataWorker,media,priority:readDetails?WorkerPriority.Foreground:WorkerPriority.Visible).FillAll(SourceRootId(row),SourceRootPath(row),SourceRootEpoch(row),null,token,entryId:row.Item.EntryId,readDetails:readDetails);
    }
}
