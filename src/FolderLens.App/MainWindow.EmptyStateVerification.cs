using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyEmptyStateAudit(string source,Dictionary<string,object> report)
    {
        var failures=new List<string>();
        suppressFilters=true;Search.Text="__no_match__";suppressFilters=false;
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        verifyCandidateBarrier=_=>throw new IOException("增量查询失败");
        try{await RefreshQuery(scanPreview:true);}finally{verifyCandidateBarrier=null;}
        if(ResultSummary.Text!="浏览结果未完成")failures.Add("增量查询失败摘要被旧匹配数量覆盖");
        foreach(bool failPublication in new[]{false,true})
        {
            suppressFilters=true;Search.Text="__no_match__";suppressFilters=false;await RefreshQuery();
            if(FilesGrid.Items.Count!=0||BrowserEmptyState.Visibility!=Visibility.Visible)throw new InvalidOperationException("没有形成空结果反例。");
            suppressFilters=true;Search.Text="";suppressFilters=false;
            if(failPublication)verifyPublishFault=()=>throw new IOException("发布故障反例");
            verifyRetirementBarrier=async()=>
            {
                Shell.UpdateLayout();await Task.Delay(30);
                if(FilesGrid.Items.Count==0)throw new InvalidOperationException("新结果未进入视图。");
                if(BrowserEmptyState.Visibility!=Visibility.Collapsed)failures.Add(failPublication?"异常恢复发布在退役期间被空提示遮挡":"正常发布在退役期间被空提示遮挡");
            };
            try{await RefreshQuery();}finally{verifyRetirementBarrier=null;verifyPublishFault=null;}
        }
        report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException(string.Join("；",failures));
        report["status"]="PASS";
    }
    private async Task VerifyBrowserEmptyState(string source,Dictionary<string,object> report)
    {
        async Task Capture(string name)
        {
            Shell.UpdateLayout();await Task.Delay(30);
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Shell);
            using var file=File.Create(Path.Combine(dataDirectory,name+".png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        }
        if(BrowserEmptyState.Visibility!=Visibility.Visible||BrowserEmptyOpen.Visibility!=Visibility.Visible||BrowserEmptyTitle.Text!="从一个文件夹开始")throw new InvalidOperationException("首次启动没有目录入口。");
        await Capture("welcome");
        var content=(FrameworkElement)BrowserEmptyState.Content;
        foreach(double width in new[]{260d,520d})
        {
            BrowserEmptyState.Width=width;Shell.UpdateLayout();
            var bounds=content.TransformToVisual(BrowserEmptyState).TransformBounds(new Windows.Foundation.Rect(0,0,content.ActualWidth,content.ActualHeight));
            if(bounds.X<0||bounds.Right>BrowserEmptyState.ActualWidth+1||Math.Abs(bounds.X+bounds.Width/2-BrowserEmptyState.ActualWidth/2)>1)throw new InvalidOperationException("空状态内容未居中或横向超出可见区域。");
        }
        BrowserEmptyState.Width=double.NaN;Shell.UpdateLayout();report["centeredWidths"]=new[]{260,520};
        string empty=Path.Combine(dataDirectory,"empty");Directory.CreateDirectory(empty);
        await OpenRoot(empty);if(metadataTask is not null)await metadataTask;
        if(BrowserEmptyState.Visibility!=Visibility.Visible||BrowserEmptyTitle.Text!="当前视图没有可显示的文件")throw new InvalidOperationException("真实空目录状态不明确。");
        await Capture("empty");
        // Presentation-only scan/cancel states: no fabricated catalog or progress.
        var priorScan=scanTask;var pendingScan=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanTask=pendingScan.Task;UpdateBrowserEmptyState();
        if(BrowserEmptyTitle.Text!="正在扫描子文件夹")throw new InvalidOperationException("活动扫描被显示为已完成的空结果。");
        CancelScan(this,new RoutedEventArgs());
        if(BrowserEmptyTitle.Text!="扫描已停止")throw new InvalidOperationException("停止后仍提示等待扫描。");
        pendingScan.TrySetResult();scanTask=priorScan;
        await OpenRoot(empty);if(metadataTask is not null)await metadataTask;
        verifyCandidateBarrier=_=>throw new UnauthorizedAccessException("无法读取当前目录。");
        try{await RefreshQuery();}finally{verifyCandidateBarrier=null;}
        if(BrowserEmptyTitle.Text!="暂时无法显示浏览结果"||BrowserEmptyDescription.Text!="无法读取当前目录。"||ResultSummary.Text!="浏览结果未完成")throw new InvalidOperationException("查询失败没有显示原因或仍显示加载中。");
        await Capture("error");
        const string newerStatus="新的目录状态";
        verifyCandidateBarrier=_=>{queryRequest++;Status.Text=newerStatus;return Task.FromException(new IOException("过期查询错误"));};
        try{await RefreshQuery();}finally{verifyCandidateBarrier=null;}
        if(Status.Text!=newerStatus||browserEmptyError is not null)throw new InvalidOperationException("旧查询错误覆盖了新的浏览状态。");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        await WaitUntil(()=>visible.Any(row=>row.Thumbnail is not null),TimeSpan.FromSeconds(10));
        if(BrowserEmptyState.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("提示遮挡了已有图片。");
        var original=FilesGrid.ItemsSource;var row=visible.First(item=>item.Thumbnail is not null);var cover=row.Thumbnail;
        ShowBrowserError(new IOException("核对目录失败，已有文件仍可浏览。"));
        if(BrowserEmptyState.Visibility!=Visibility.Collapsed||!ReferenceEquals(original,FilesGrid.ItemsSource)||!ReferenceEquals(cover,row.Thumbnail))throw new InvalidOperationException("错误提示清除或遮挡了已有内容。");
        report["realEmptyAndRecovery"]=true;report["presentationScanAndCancel"]=true;
        report["queryFailureAndStaleGuard"]=true;report["existingResultsUnaffected"]=true;report["status"]="PASS";
    }
}
