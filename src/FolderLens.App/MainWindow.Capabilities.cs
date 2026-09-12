using FolderLens.Contracts;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Task? capabilityTask;
    private RuntimeCapabilities? runtimeCapabilities;
    private string capabilityStatus="组件检测尚未开始。";
    private async Task ReadRuntimeCapabilities()
    {
        if(metadataWorker is null)return;
        capabilityStatus="正在后台检测媒体组件…";
        try
        {
            var report=await metadataWorker.GetRuntimeCapabilities(lifetime.Token);if(closing)return;runtimeCapabilities=report;
            capabilityStatus=$"libvips {report.VipsVersion} · RawBridge ABI {report.RawBridgeAbi?.ToString()??"不可用"}\n"+string.Join("\n",report.Formats.Select(f=>$"{f.Format.ToUpperInvariant()}：{(f.BasicDecodeStatus=="PASS"?"基础解码通过":f.BasicDecodeStatus=="FAIL"?"基础解码失败":f.ProviderAvailable?"组件可用，样本未验证":"组件不可用")}"));
            capabilityStatus+="\n基础检测不代表所有编码、相机和色彩变体均已验证。";
            if(settings is not null)await settings.Save("capabilities.json",report);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!closing)capabilityStatus="无法检测媒体组件："+ex.GetType().Name+"。可重新检测，或检查程序组件是否齐全。";}
    }
}
