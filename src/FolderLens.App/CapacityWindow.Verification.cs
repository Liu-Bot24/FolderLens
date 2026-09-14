using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class CapacityWindow
{
    internal async Task VerifyPresentation(string output,Dictionary<string,object> result)
    {
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
            result[$"bars-{width}"]=bars.Select(bar=>new{bar.Value,bar.IsIndeterminate}).ToArray();
            if(bars.Length==0||bars.Any(bar=>bar.IsIndeterminate))throw new InvalidOperationException("容量占比条没有使用确定数值模式。");
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
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Roots SET scan_state='cancelled' WHERE root_id=$root; UPDATE SchemaInfo SET catalog_revision=catalog_revision+1";cmd.Parameters.AddWithValue("$root",rootId);return cmd.ExecuteNonQuery();});
        await CheckForUpdates();await work;
        if(!state.Text.Contains("扫描已取消"))throw new InvalidOperationException("取消的扫描没有明确显示终止状态。");
        var unfinished=((IEnumerable<CapacityDisplayRow>)ranking.ItemsSource).Single(row=>row.Value.RelativePath=="B");
        if(unfinished.Size=="0 B"||unfinished.Percent=="0.0%")throw new InvalidOperationException("不完整统计把未统计目录显示为确定的零容量。");
        result["cancelledStateAutoUpdated"]=true;result["unfinishedZeroNotPresentedAsEmpty"]=true;
        scope.SelectedIndex=1;await work;
        if(files.Text!="12"||!state.Text.Contains("筛选结果"))throw new InvalidOperationException("固定筛选结果统计没有显示。");
        result["fileCount"]=12;result["themeChangeRetainedRows"]=true;result["drillReturnAndSnapshotScope"]=true;result["status"]="PASS";
    }
}

public sealed partial class MainWindow
{
    private async Task VerifyCapacityUi(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        var snapshot=resultHandle!;if(!await catalog!.RetainSnapshot(snapshot.Id))throw new InvalidOperationException("无法保留容量快照。");
        CapacityWindow? window=null;
        try
        {
            window=new CapacityWindow(catalog,rootId,root,snapshot,lifetime.Token,(_,_)=>Task.CompletedTask);
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-16000,-16000));window.AppWindow.Show(false);
            await window.VerifyPresentation(dataDirectory,report);
        }
        finally{if(window is not null){window.Close();await window.ShutdownAsync();}else await catalog.ReleaseSnapshot(snapshot.Id);}
    }
}
