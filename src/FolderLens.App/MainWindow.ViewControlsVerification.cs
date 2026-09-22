using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyViewControls(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        // Initial watcher reconciliation can still publish after OpenRoot returns.
        // Start interaction assertions only once those real publications settle.
        var settling=System.Diagnostics.Stopwatch.StartNew();
        var quiet=System.Diagnostics.Stopwatch.StartNew();long lastQuery=queryRequest;
        while(quiet.Elapsed<TimeSpan.FromMilliseconds(1500))
        {
            if(settling.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("初始文件列表未稳定。");
            await Task.Delay(50);
            if(lastQuery!=queryRequest||queryBusy||reconcilePending||scanTask is {IsCompleted:false}){lastQuery=queryRequest;quiet.Restart();}
        }
        await SelectBrowserOrdinal(results!,5,lifetime.Token);
        syncingBrowserSelection=true;
        try{FilesGrid.SelectRange(new ItemIndexRange(4,3));}finally{syncingBrowserSelection=false;}
        monitor?.Dispose();monitor=null;
        var ranges=SelectedOrdinals(FilesGrid).ToArray();var selection=selected;
        var transitions=new List<object>();report["transitions"]=transitions;
        foreach(bool details in new[]{true,true,false,false,true,false})
        {
            var option=details?DetailsMode:ThumbnailMode;
            // Use the control's supported automation pattern so the native
            // checked-state transition and application click handler both run.
            var peer=new ToggleButtonAutomationPeer(option);
            ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
            await Task.Delay(60);Shell.UpdateLayout();
            transitions.Add(new{details,ranges,actual=SelectedOrdinals(ActiveBrowser),before=selection?.Ordinal,after=selected?.Ordinal,same=ReferenceEquals(selection,selected),lastQuery,queryRequest});
            if(DetailsMode.IsChecked!=details||ThumbnailMode.IsChecked==details||DetailsPane.Visibility!=(details?Visibility.Visible:Visibility.Collapsed))throw new InvalidOperationException("视图切换选项不是互斥状态。");
            if(!ranges.SequenceEqual(SelectedOrdinals(ActiveBrowser))||!ReferenceEquals(selected,selection))throw new InvalidOperationException("切换或重复点击当前视图丢失选区。");
            if((details?(ListViewBase)FilesGrid:FilesList).ItemsSource is not null)throw new InvalidOperationException("隐藏视图仍订阅列表。");
            if(categoryDetailViews[Tag(Category)]!=details)throw new InvalidOperationException("手动选择未保存分类视图偏好。");
        }
        var measurements=new List<object>();double priorWidth=PreviewColumn.Width.Value;
        try
        {
            foreach(string style in new[]{"native","soft"})
            {
                ApplyAppearance(new(style));
                foreach(int width in new[]{220,300,650})
                {
                    PreviewColumn.Width=new(width);Shell.UpdateLayout();await Task.Delay(60);
                    var tree=TreePane.TransformToVisual(Shell).TransformBounds(new(0,0,TreePane.ActualWidth,TreePane.ActualHeight));
                    var preview=PreviewPane.TransformToVisual(Shell).TransformBounds(new(0,0,PreviewPane.ActualWidth,PreviewPane.ActualHeight));
                    measurements.Add(new{style,width,treeLeft=tree.Left,previewLeft=preview.Left,treeRight=tree.Right,previewRight=preview.Right});
                    if(Math.Abs(tree.Left-preview.Left)>.5||Math.Abs(tree.Right-preview.Right)>.5)throw new InvalidOperationException("文件夹和预览面板左右边界不一致。");
                }
            }
        }
        finally{PreviewColumn.Width=new(priorWidth);}
        Shell.UpdateLayout();
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Shell);
        using var file=File.Create(Path.Combine(dataDirectory,"view-controls.png"));using var stream=file.AsRandomAccessStream();
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        report["sidebarAlignment"]=measurements;report["exclusiveOptionsAndRepeatedClicks"]=true;report["selectionAndCategoryPreference"]=true;report["status"]="PASS";
    }
}
