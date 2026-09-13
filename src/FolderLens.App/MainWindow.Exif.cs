using System.Globalization;
using System.Text.Json;
using FolderLens.Contracts;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task LoadSelectedExif(FileRow row,long current,CancellationToken token)
    {
        if(catalog is null||previewWorker is null||row.Item is null||selectedProperties?.Details is {State:not "notRequested"}||row.HydrationState=="placeholder")return;
        try
        {
            var context=Context(row,current);
            var reply=await previewWorker.Request(Path.Combine(root,row.RelativePath),"probe",context,new(),token,Stamp(row));
            if(current!=selection||closing||token.IsCancellationRequested)return;
            var metadata=reply.Message.Metadata!.Value;
            var details=metadata.GetProperty("details").Deserialize<ContentMetadataDetails>(WorkerProtocol.Json)??throw new InvalidDataException("EXIF 信息未返回。");
            if(!await catalog.ApplyFileDetails(row.Item.EntryId,row.Item.Version,context.RootId,context.RootEpoch,details,metadata.GetProperty("provider").GetString()!,token))return;
            if(current!=selection||closing||token.IsCancellationRequested)return;
            if(selectedProperties is {} properties)selectedProperties=properties with{Details=details};
            UpdateViewerInformation();
        }
        catch(OperationCanceledException){}
        catch(Exception error)
        {
            if(current==selection&&!closing&&selectedProperties is {} properties)
            {selectedProperties=properties with{Details=new(){State="failed",ErrorCode=error.Message}};UpdateViewerInformation();}
        }
    }
    private static string FormatViewerExif(ContentMetadataDetails? details)
    {
        if(details is null||details.State=="notRequested")return "";
        var lines=new List<string>();
        void Add(string label,string? value){if(!string.IsNullOrWhiteSpace(value))lines.Add(label+"："+value);}
        Add("相机",string.Join(' ',new[]{details.CameraMake,details.CameraModel}.Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct()));
        Add("镜头",details.LensModel);
        Add("拍摄时间",details.Capture.WallTicks is {} ticks?new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture):null);
        Add("ISO",details.Iso?.ToString(CultureInfo.InvariantCulture));
        Add("快门",details.ExposureRational is {} rational?rational+" s":details.ExposureSeconds is {} seconds?seconds.ToString("0.######",CultureInfo.InvariantCulture)+" s":null);
        Add("光圈",details.Aperture is {} aperture?$"f/{aperture:0.#}":null);
        Add("焦距",details.FocalLengthMm is {} focal?$"{focal:0.#} mm":null);
        if(details.State=="failed")lines.Add("部分 EXIF 信息无法读取");
        return lines.Count>0?"EXIF\n"+string.Join('\n',lines):"未检测到拍摄 EXIF 信息";
    }
}
