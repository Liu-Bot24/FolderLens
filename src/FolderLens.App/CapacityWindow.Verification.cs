using System.Runtime.InteropServices.WindowsRuntime;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class CapacityWindow
{
    internal async Task VerifyPhysicalCapacity(Dictionary<string,object> result)
    {
        await physicalWork.WaitAsync(TimeSpan.FromSeconds(20));await work.WaitAsync(TimeSpan.FromSeconds(10));
        if(report is not {IsComplete:true}||files.Text!="14"||!state.Text.Contains("统计完成"))
            throw new InvalidOperationException("独立容量统计未包含浏览索引之外的新文件。");
        long scan=physicalGeneration;
        depthChoice.SelectedIndex=1;await work.WaitAsync(TimeSpan.FromSeconds(10));
        var ranked=((IEnumerable<CapacityDisplayRow>)ranking.ItemsSource).Select(row=>row.Value.RelativePath).ToArray();
        if(physicalGeneration!=scan||ranked.Length!=3||!ranked.Contains(@"A\inner")||ranked.Contains("A")||!ranked.Contains("B")||!ranked.Contains("capacity-only"))
            throw new InvalidOperationException("二级排名同时列出父子目录，或浅分支没有保留末级目录。");
        var distribution=((IEnumerable<CapacityDisplayRow>)shares.ItemsSource).Select(row=>row.Value).ToArray();
        if(distribution.Sum(row=>row.KnownBytes)!=report.Rows.Single(row=>row.RelativePath=="").SubtreeLogical||
            distribution.Single(row=>row.IsDirectFiles).Files!=12)
            throw new InvalidOperationException("二级容量分布重复统计父子目录，或遗漏上层直属文件。");
        depthChoice.SelectedIndex=2;await work.WaitAsync(TimeSpan.FromSeconds(10));
        if(physicalGeneration!=scan||!((IEnumerable<CapacityDisplayRow>)ranking.ItemsSource).Any(row=>row.Value.RelativePath==@"A\inner"))
            throw new InvalidOperationException("超过实际深度后未显示最深目录，或切换层级触发重复扫描。");
        await File.WriteAllBytesAsync(Path.Combine(physicalRoot,"capacity-only","new.txt"),[3]);
        RestartPhysicalScan();QueueRender();await physicalWork.WaitAsync(TimeSpan.FromSeconds(20));await work.WaitAsync(TimeSpan.FromSeconds(10));
        if(report is not {IsComplete:true}||files.Text!="15")
            throw new InvalidOperationException("刷新目录容量后未读取新文件。");
        result["physicalOutsideCatalog"]=true;result["levelSwitchWithoutRescan"]=true;result["refreshReadsChanges"]=true;result["status"]="PASS";
    }

    internal async Task VerifyPresentation(string output,Dictionary<string,object> result)
    {
        if(physicalCapacity)await physicalWork.WaitAsync(TimeSpan.FromSeconds(20));
        await work;
        var page=(Microsoft.UI.Xaml.Controls.ScrollViewer)Content;
        var root=(Microsoft.UI.Xaml.Controls.Grid)page.Content;
        for(int i=0;i<60&&!root.IsLoaded;i++)await Task.Delay(50);
        root.UpdateLayout();
        var expected=Application.Current.RequestedTheme==ApplicationTheme.Dark?ElementTheme.Dark:ElementTheme.Light;
        result["theme"]=root.ActualTheme.ToString();
        if(root.RequestedTheme!=ElementTheme.Default||root.ActualTheme!=expected)throw new InvalidOperationException("容量看板没有继承应用主题。");
        if(!hasDisplayedPage||files.Text!="12")throw new InvalidOperationException("真实索引统计未显示预期文件数。");
        var rows=ranking.ItemsSource;var background=((SolidColorBrush)root.Background).Color;
        root.RequestedTheme=expected==ElementTheme.Dark?ElementTheme.Light:ElementTheme.Dark;root.UpdateLayout();await Task.Delay(30);
        if(((SolidColorBrush)root.Background).Color==background||!ReferenceEquals(rows,ranking.ItemsSource))throw new InvalidOperationException("看板主题没有变化或重建了统计结果。");
        root.RequestedTheme=ElementTheme.Default;root.UpdateLayout();
        foreach(int width in new[]{1600,900})
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width,1000));await Task.Delay(60);root.UpdateLayout();
            static IEnumerable<DependencyObject> Children(DependencyObject node)
            {
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);yield return child;foreach(var nested in Children(child))yield return nested;}
            }
            var bars=Children(shares).OfType<Microsoft.UI.Xaml.Controls.ProgressBar>().ToArray();
            // Theme/size changes realize the ItemsControl template asynchronously.
            // Wait for the expected controls, not an assumed 60 ms render deadline.
            for(int attempt=0;attempt<60&&(bars.Length==0||bars.Length!=shares.Items.Count);attempt++)
            {
                await Task.Delay(50);root.UpdateLayout();
                bars=Children(shares).OfType<Microsoft.UI.Xaml.Controls.ProgressBar>().ToArray();
            }
            result[$"expectedBars-{width}"]=shares.Items.Count;
            result[$"bars-{width}"]=bars.Select(bar=>new{bar.Value,bar.IsIndeterminate}).ToArray();
            if(bars.Length==0||bars.Length!=shares.Items.Count)throw new InvalidOperationException("容量占比条未在等待期内完整实现。");
            if(bars.Any(bar=>bar.IsIndeterminate))throw new InvalidOperationException("容量占比条没有使用确定数值模式。");
            foreach(var button in new[]{parent,refresh,browseButton})
            {
                var bounds=button.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0,0,button.ActualWidth,button.ActualHeight));
                if(bounds.X<0||bounds.Right>root.ActualWidth+1)throw new InvalidOperationException("容量工具按钮在窄窗被横向裁切。");
            }
            if(width==900&&(ranking.ActualHeight<200||page.ScrollableHeight<=0))throw new InvalidOperationException("窄窗没有提供可用排名高度和整页滚动。");
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(root);
            using var file=File.Create(Path.Combine(output,$"capacity-{width}.png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        }
        var child=((IEnumerable<CapacityDisplayRow>)ranking.ItemsSource).First(row=>row.Value.RelativePath=="A");ranking.SelectedItem=child;
        Navigate("A");await work;
        if(directory!="A"||!parent.IsEnabled||files.Text!="12")throw new InvalidOperationException("看板下钻结果错误。");
        Navigate("");await work;
        if((ranking.SelectedItem as CapacityDisplayRow)?.Value.RelativePath!="A")throw new InvalidOperationException("返回上层没有恢复目录选择。");
        depthChoice.SelectedIndex=2;await work;
        if(files.Text!="12"||!((IEnumerable<CapacityDisplayRow>)ranking.ItemsSource).Any(row=>row.Value.RelativePath=="A"))
            throw new InvalidOperationException("超出实际层数后没有保留最深文件夹。");
        result["fileCount"]=12;result["themeChangeRetainedRows"]=true;result["drillReturnAndDepthFallback"]=true;result["status"]="PASS";
    }
}

public sealed partial class MainWindow
{
    private async Task VerifyPhysicalCapacityUi(string source,Dictionary<string,object> report)
    {
        await StopFixtureScan(source);
        string added=Path.Combine(source,"capacity-only","unindexed.txt");Directory.CreateDirectory(Path.GetDirectoryName(added)!);await File.WriteAllBytesAsync(added,new byte[7]);
        string deeper=Path.Combine(source,"A","inner");Directory.CreateDirectory(deeper);await File.WriteAllBytesAsync(Path.Combine(deeper,"unindexed.txt"),new byte[5]);
        CapacityWindow? window=null;
        try
        {
            window=new CapacityWindow(catalog!,rootId,source,lifetime.Token,(_,_)=>Task.CompletedTask,
                physicalCapacity:true,scanWorkerExecutable:ScanWorkerClient.FindExecutable(ScanWorkerDirectory));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-16000,-16000));window.AppWindow.Show(false);
            await window.VerifyPhysicalCapacity(report);
            capacityWindow=window;
            await OpenRoot(Path.Combine(source,"A"));
            await window.ShutdownAsync();
            if(!window.IsClosing||root!=Path.Combine(source,"A"))
                throw new InvalidOperationException("切换主文件夹后旧目录容量统计未退休。");
            report["navigationRetiresCapacityWindow"]=true;
        }
        finally{if(ReferenceEquals(capacityWindow,window))capacityWindow=null;if(window is not null){if(!window.IsClosing)window.Close();await window.ShutdownAsync();}}
    }

    private async Task VerifyCapacityUi(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        CapacityWindow? window=null;
        try
        {
            window=new CapacityWindow(catalog!,rootId,root,lifetime.Token,(_,_)=>Task.CompletedTask,physicalCapacity:true);
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-16000,-16000));window.AppWindow.Show(false);
            await window.VerifyPresentation(dataDirectory,report);
        }
        finally{if(window is not null){window.Close();await window.ShutdownAsync();}}
    }
}
