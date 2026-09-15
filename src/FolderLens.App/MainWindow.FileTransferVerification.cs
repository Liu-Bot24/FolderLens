using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFileTransfer(string fixture,Dictionary<string,object> report)
    {
        await OpenRoot(fixture);if(scanTask is not null)await scanTask;await RefreshQuery();
        await WaitUntil(()=>!queryBusy&&!replacingRoot,TimeSpan.FromSeconds(5));
        foreach(bool details in new[]{false,true})
        {
            DetailsMode.IsChecked=details;ToggleView(DetailsMode,new());
            var view=ActiveBrowser;view.DeselectRange(new ItemIndexRange(0,(uint)view.Items.Count));view.SelectRange(new ItemIndexRange(0,2));
            var files=await TransferStorageItems();if(files.Length!=2||!view.CanDragItems||view.CanReorderItems)throw new InvalidOperationException("两种列表的多选文件传输未接通。");
            var data=new DataPackage{RequestedOperation=DataPackageOperation.Copy};data.SetStorageItems(files);
            if((await data.GetView().GetStorageItemsAsync()).Count!=2)throw new InvalidOperationException("系统文件数据包没有保留多选。");
            var delayed=new DataPackage{RequestedOperation=DataPackageOperation.Copy|DataPackageOperation.Move};
            delayed.SetDataProvider(StandardDataFormats.StorageItems,request=>request.SetData(files));
            if((await delayed.GetView().GetStorageItemsAsync()).Count!=2)throw new InvalidOperationException("拖动数据包延迟提供失败。");
            string target=Path.Combine(dataDirectory,details?"paste-details":"paste-grid");Directory.CreateDirectory(target);
            await ReceiveFiles(data.GetView(),target,null);
            if(Directory.GetFiles(target).Length!=2||files.Any(f=>!File.Exists(f.Path)))throw new InvalidOperationException("复制粘贴未完成或改变了原文件。");
            string moved=Path.Combine(dataDirectory,details?"cut-details":"cut-grid");Directory.CreateDirectory(moved);
            var cut=new DataPackage{RequestedOperation=DataPackageOperation.Move};
            var copied=new List<IStorageItem>();foreach(var path in Directory.GetFiles(target))copied.Add(await StorageFile.GetFileFromPathAsync(path));cut.SetStorageItems(copied);
            await ReceiveFiles(cut.GetView(),moved,null);
            if(Directory.GetFiles(target).Length!=0||Directory.GetFiles(moved).Length!=2)throw new InvalidOperationException("剪切粘贴未完成。");
            report[details?"detailsMultiSelectionCopyCutPaste":"thumbnailMultiSelectionCopyCutPaste"]=true;
        }
        report["windowsExplorerPhysicalDrag"]="NOT_RUN";
        report["clipboardOwnershipAndExplorerPaste"]="NOT_RUN";
        report["status"]="PASS";
    }
}
