using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyGroupHeaderLayout(string source,byte[] png,Dictionary<string,object> report)
    {
        var args=Environment.GetCommandLineArgs();
        ApplyAppearance(new(args.Contains("--verify-native")?"native":"soft"));
        bool small=!args.Contains("--verify-large-groups");
        var labels=small?Enumerable.Range(0,24).Select(n=>$"分组{n:D2} "+string.Concat(Enumerable.Repeat("示例文件夹名称 空格 ❤️ 说明文字 ",5)).TrimEnd()).ToArray():new[]{"短标题", "长标题 "+string.Concat(Enumerable.Repeat("示例文件夹名称 空格 ❤️ 说明文字 ",5)).TrimEnd(), "末尾分组"};
        if(Environment.GetCommandLineArgs().Contains("--verify-short-titles"))labels=Enumerable.Range(0,labels.Length).Select(n=>$"分组{n:D2}").ToArray();
        foreach(string label in labels)
        {
            string directory=Path.Combine(source,label);Directory.CreateDirectory(directory);
            int count=small?1+Array.IndexOf(labels,label)%7:48;
            for(int n=0;n<count;n++)await File.WriteAllBytesAsync(Path.Combine(directory,$"image-{n:D3}.png"),png);
        }
        folderGrouping=new(true);UpdateGroupingButton();
        suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
        await OpenRoot(source);if(scanTask is not null)await scanTask;await RefreshQuery();
        monitor?.Dispose();monitor=null;
        await Task.Delay(500);
        // Re-enable the old behavior only for the negative control. Normal runs
        // exercise the shipped XAML setting without overriding it in the test.
        if(args.Contains("--verify-legacy-sticky"))((ItemsWrapGrid)FilesGrid.ItemsPanelRoot).AreStickyGroupHeadersEnabled=true;
        IEnumerable<FrameworkElement> Elements(DependencyObject parent)
        {
            for(int n=0;n<VisualTreeHelper.GetChildrenCount(parent);n++)
            {
                var child=VisualTreeHelper.GetChild(parent,n);
                if(child is FrameworkElement element)yield return element;
                foreach(var nested in Elements(child))yield return nested;
            }
        }
        object Bounds(FrameworkElement element)
        {
            var p=element.TransformToVisual(FilesGrid).TransformPoint(new(0,0));
            return new{type=element.GetType().Name,x=p.X,y=p.Y,w=element.ActualWidth,h=element.ActualHeight,desiredW=element.DesiredSize.Width,desiredH=element.DesiredSize.Height};
        }
        var samples=new List<object>();int escaped=0;
        var overlaps=new List<object>();int frames=0,visibleTitles=0;
        void Frame(object? sender,object args)
        {
            frames++;var all=Elements(FilesGrid).ToArray();
            foreach(var title in all.OfType<TextBlock>().Where(t=>labels.Contains(t.Text)))
            {
                var p=title.TransformToVisual(FilesGrid.ItemsPanelRoot).TransformPoint(new(0,0));
                var screen=title.TransformToVisual(FilesGrid).TransformPoint(new(0,0));
                if(screen.X<0||screen.Y<80||screen.Y>FilesGrid.ActualHeight)continue;
                visibleTitles++;
                foreach(var card in all.OfType<GridViewItem>())
                {
                    var q=card.TransformToVisual(FilesGrid.ItemsPanelRoot).TransformPoint(new(0,0));
                    if(q.X<0||q.X>=p.X+title.ActualWidth||q.X+card.ActualWidth<=p.X)continue;
                    if(q.Y+card.ActualHeight>p.Y&&q.Y<p.Y+title.ActualHeight&&overlaps.Count<40)
                        overlaps.Add(new{frame=frames,titleY=p.Y,titleH=title.ActualHeight,cardY=q.Y,cardH=card.ActualHeight,screenY=screen.Y,offset=FindScrollViewer(FilesGrid)?.VerticalOffset});
                }
            }
        }
        CompositionTarget.Rendering+=Frame;
        try
        {
        foreach(int size in args.Contains("--verify-minimal")?new[]{160}:new[]{160,300,100,240})
        {
            ThumbnailSize.Value=size;UpdateGridPresentation();Shell.UpdateLayout();await Task.Delay(200);
            var scroll=FindScrollViewer(FilesGrid)!;
            foreach(double offset in new[]{0d,120,350,700,1100,1800,2400,3100,4000,5200,7000,0})
            {
                scroll.ChangeView(null,offset,null,!small);await Task.Delay(small?350:120);Shell.UpdateLayout();
                if(offset==350&&!args.Contains("--verify-no-update"))
                {
                    string extra=Path.Combine(source,labels[1],$"added-{size}.png");await File.WriteAllBytesAsync(extra,png);
                    await new FolderLens.Infrastructure.DirectoryIndexer(catalog!).Scan(rootId,root,epoch,true,[],null,lifetime.Token);
                    await RefreshQuery(preserveViewport:true,scanPreview:true);await Task.Delay(200);
                }
                var all=Elements(FilesGrid).ToArray();
                var titles=all.OfType<TextBlock>().Where(t=>labels.Contains(t.Text)).ToArray();
                var details=new List<object>();
                foreach(var title in titles)
                {
                    DependencyObject? p=VisualTreeHelper.GetParent(title);
                    var chain=new List<object>();FrameworkElement? header=null;
                    while(p is FrameworkElement e&&e!=FilesGrid)
                    {
                        chain.Add(Bounds(e));if(e.GetType().Name=="GridViewHeaderItem"){header=e;break;}
                        p=VisualTreeHelper.GetParent(e);
                    }
                    if(header is not null)
                    {
                        var point=title.TransformToVisual(header).TransformPoint(new(0,0));
                        if(point.Y< -1||point.Y+title.ActualHeight>header.ActualHeight+1)escaped++;
                    }
                    details.Add(new{title=title.Text,bounds=Bounds(title),ancestors=chain});
                }
                samples.Add(new{size,offset=scroll.VerticalOffset,viewport=scroll.ViewportHeight,headers=details,cards=all.OfType<GridViewItem>().Select(Bounds).ToArray()});
                if(size==300&&(offset==350||offset==1100))
                {
                    var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(BrowserPane);
                    using var file=File.Create(Path.Combine(dataDirectory,$"headers-{size}-{offset}.png"));using var stream=file.AsRandomAccessStream();
                    var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
                }
            }
        }
        }
        finally{CompositionTarget.Rendering-=Frame;}
        report["samples"]=samples;report["frames"]=frames;report["visibleTitleObservations"]=visibleTitles;report["middleOverlaps"]=overlaps;report["escapedTitleCount"]=escaped;
        if(frames<10||visibleTitles==0)throw new InvalidOperationException("分组标题滚动检查没有观察到足够的实际画面。");
        if(escaped!=0||overlaps.Count!=0)throw new InvalidOperationException("滚动时分组标题越过自身边界或覆盖了列表中部的文件卡片。");
        report["status"]="PASS";
    }
}
