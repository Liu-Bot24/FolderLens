using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyVisibleGroupMove(string source,byte[] png,Dictionary<string,object> report)
    {
        folderGrouping=new(true);UpdateGroupingButton();
        for(int i=12;i<300;i++)await File.WriteAllBytesAsync(Path.Combine(source,"A",$"image-{i:D4}.png"),png);
        await File.WriteAllBytesAsync(Path.Combine(source,"B","first.png"),png);
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        await WaitUntil(()=>visible.Any(row=>row.Thumbnail is not null),TimeSpan.FromSeconds(15));
        var anchor=visible.Where(row=>row.Thumbnail is not null).OrderBy(row=>row.Ordinal).First();
        var originalContainer=(FrameworkElement)FilesGrid.ContainerFromItem(anchor);var originalImage=anchor.Thumbnail;
        double top=originalContainer.TransformToVisual(FilesGrid).TransformPoint(new(0,0)).Y;
        // This group becomes larger than the group currently on screen.
        monitor?.Dispose();monitor=null;
        for(int i=0;i<1000;i++)await File.WriteAllBytesAsync(Path.Combine(source,"B",$"new-{i:D4}.png"),png);
        await new DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);
        int frames=0,missing=0,moved=0,cleared=0;
        void Frame(object? sender,object args)
        {
            frames++;var container=FilesGrid.ContainerFromItem(anchor) as FrameworkElement;
            if(container is null){missing++;return;}
            if(Math.Abs(container.TransformToVisual(FilesGrid).TransformPoint(new(0,0)).Y-top)>2)moved++;
            if(anchor.Thumbnail is null)cleared++;
        }
        CompositionTarget.Rendering+=Frame;
        try{await RefreshQuery(preserveViewport:true,scanPreview:true);await Task.Delay(500);}
        finally{CompositionTarget.Rendering-=Frame;}
        report["frames"]=frames;report["anchorMissingFrames"]=missing;report["anchorMovedFrames"]=moved;report["anchorClearedFrames"]=cleared;
        report["originalThumbnailRetained"]=ReferenceEquals(anchor.Thumbnail,originalImage);report["groupOrder"]=browserGroups!.Select(group=>group.Title).ToArray();
        if(frames==0||missing+moved+cleared>0||!ReferenceEquals(anchor.Thumbnail,originalImage))throw new InvalidOperationException("后台分组换序使静止视口跳动或图片消失。");
        if(browserGroups![0].Title!="B")throw new InvalidOperationException("未应用新的分组容量顺序。");
        report["status"]="PASS";
    }
}
