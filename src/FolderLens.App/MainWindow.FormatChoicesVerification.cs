using FolderLens.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFormatChoices(string source,Dictionary<string,object> report)
    {
        await File.WriteAllTextAsync(Path.Combine(source,"example.TXT"),"extension selection");
        await File.WriteAllTextAsync(Path.Combine(source,"readme"),"no extension");
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;
        await RefreshQuery();await LoadFormatChoices();
        if(FormatPicker is not DropDownButton||FormatOptions.SelectionMode!=ListViewSelectionMode.Multiple)
            throw new InvalidOperationException("格式没有使用可多选的选择控件。");
        if(FormatPicker.HorizontalContentAlignment!=HorizontalAlignment.Left||FormatChoicesFlyout.Placement!=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft)
            throw new InvalidOperationException("格式文字与弹层没有沿左边缘对齐。");
        if(WindowPreviewButton.Parent!=FullScreenPreviewButton.Parent||!PreviewHeader.Children.Contains((UIElement)WindowPreviewButton.Parent))
            throw new InvalidOperationException("两种预览入口没有集中在左侧预览区域。");
        if(!double.IsNaN(PreviewActual.Width)||PreviewActual.MinWidth<48)
            throw new InvalidOperationException("100%仍使用图标按钮的固定窄宽度。");
        var legacySearch=CurrentFilter() with{SearchScope="nameAndPath"};ApplySavedFilter(legacySearch);
        if(CurrentFilter().SearchScope!="name")throw new InvalidOperationException("旧视图仍恢复了路径搜索。");
        var beforeRestore=CaptureView();long beforeRootVersion=rootChangeVersion,beforeEpoch=epoch;var beforeScan=scanTask;
        await RestoreSavedView(beforeRestore with{Filter=beforeRestore.Filter with{Recursive=false}},recordHistory:false);
        if(rootChangeVersion!=beforeRootVersion||epoch!=beforeEpoch||!ReferenceEquals(scanTask,beforeScan)||CurrentFilter().MaxFolderLevels!=1)
            throw new InvalidOperationException("恢复旧单层视图启动了重扫或没有映射为1层。");
        if((await catalog!.ReadPage(resultHandle!.Id,0)).Any(row=>row.RelativePath.Contains('\\')))
            throw new InvalidOperationException("恢复旧单层视图显示了更深文件。");
        await RestoreSavedView(beforeRestore,recordHistory:false);
        report["legacyRestoreKeepsScanEpochAndTask"]=true;
        var imageOptions=FormatOptions.Items.Cast<ExtensionOption>().ToArray();
        if(imageOptions.Length!=1||imageOptions[0].Extension!="png")throw new InvalidOperationException("图片分类扩展名不正确。");
        FormatOptions.SelectedItems.Add(imageOptions[0]);
        if(!CurrentFilter().FileExtensions.SequenceEqual(new[]{"png"}))throw new InvalidOperationException("格式选择没有进入筛选。");
        await ApplyBrowserFilters();if(results?.Count!=12)throw new InvalidOperationException("扩展名筛选结果不正确。");
        var saved=CurrentFilter();SetFormatChoices(new());ApplySavedFilter(saved);
        if(!CurrentFilter().FileExtensions.SequenceEqual(saved.FileExtensions))throw new InvalidOperationException("保存的格式选择未恢复。");
        ApplySavedFilter(new FilterSpec{RootId=rootId,Kinds=[]});await RefreshQuery();await LoadFormatChoices();
        var all=FormatOptions.Items.Cast<ExtensionOption>().ToArray();
        if(!all.Select(item=>item.Extension).SequenceEqual(new[]{"","png","txt"}))throw new InvalidOperationException("全部文件未自动识别扩展名。");
        FormatOptions.SelectedItems.Add(all.Single(item=>item.Extension=="txt"));
        FormatOptions.SelectedItems.Add(all.Single(item=>item.Extension.Length==0));
        await ApplyBrowserFilters();if(results?.Count!=2)throw new InvalidOperationException("多选或无扩展名筛选错误。");
        await LoadFormatChoices();if(FormatOptions.Items.Count!=3)throw new InvalidOperationException("筛选后丢失其他可选格式。");
        ApplySavedFilter(CurrentFilter() with{Formats=["jpeg"],FileExtensions=[]});
        if(!CurrentFilter().Formats.SequenceEqual(new[]{"jpeg"}))throw new InvalidOperationException("旧保存格式不兼容。");
        await LoadFormatChoices();FormatOptions.SelectedItems.Add(FormatOptions.Items.Cast<ExtensionOption>().Single(item=>item.Extension=="png"));
        if(CurrentFilter().Formats.Length!=0)throw new InvalidOperationException("选择扩展名后未替换旧内容格式。");
        ClearFormatChoices(FormatPicker,new());await ApplyBrowserFilters();
        if(results?.Count!=14||CurrentFilter().FileExtensions.Length!=0||CurrentFilter().Formats.Length!=0)throw new InvalidOperationException("全部格式没有清除格式限制。");
        await LoadFormatChoices();
        // Lay out the real flyout content inside the offscreen verification window.
        // Opening a Popup could clamp its position onto the user's visible desktop.
        var panel=(StackPanel)FormatChoicesFlyout.Content;FormatChoicesFlyout.Content=null;
        panel.Background=(Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        Grid.SetRowSpan(panel,10);Grid.SetColumnSpan(panel,10);
        panel.HorizontalAlignment=HorizontalAlignment.Left;panel.VerticalAlignment=VerticalAlignment.Top;
        Shell.Children.Add(panel);
        try
        {
            Shell.UpdateLayout();await Task.Delay(50);
            static IEnumerable<DependencyObject> Children(DependencyObject parent)
            {
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}
            }
            var labels=Children(FormatOptions).OfType<TextBlock>().Select(text=>text.Text).ToArray();
            if(!labels.Contains(".PNG")||!labels.Contains(".TXT")||!labels.Contains("无扩展名"))throw new InvalidOperationException("扩展名没有显示在原生列表模板中。");
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(panel);
            using var file=File.Create(Path.Combine(dataDirectory,"format-options.png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
            report["nativeLabelsRendered"]=true;
        }
        finally{Shell.Children.Remove(panel);FormatChoicesFlyout.Content=panel;}
        report["automaticExtensions"]=all.Select(item=>item.Label).ToArray();report["multiSelectAndClear"]=true;
        report["categoryAndSavedFilter"]=true;report["status"]="PASS";
    }
    private async Task VerifyFilterPanelLayout(Dictionary<string,object> report)
    {
        FilterFlyout.Content=null;FilterPanel.Background=(Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];Grid.SetRowSpan(FilterPanel,10);Grid.SetColumnSpan(FilterPanel,10);
        FilterPanel.HorizontalAlignment=HorizontalAlignment.Left;FilterPanel.VerticalAlignment=VerticalAlignment.Top;Shell.Children.Add(FilterPanel);
        var cases=new List<object>();
        try
        {
            foreach(var (width,height) in new[]{(520d,620d),(360d,320d)})
            {
                FilterPanel.Width=width;FilterPanel.MaxHeight=height;Shell.UpdateLayout();await Task.Delay(50);
                foreach(var button in new[]{ApplyFiltersButton,ClearFiltersButton})
                {
                    var rect=button.TransformToVisual(FilterPanel).TransformBounds(new(0,0,button.ActualWidth,button.ActualHeight));
                    if(rect.Top<0||rect.Bottom>FilterPanel.ActualHeight+1||rect.Left<0||rect.Right>FilterPanel.ActualWidth+1)throw new InvalidOperationException("筛选操作按钮不在面板可见范围内。");
                }
                if(width==520&&FilterFieldsScroll.ScrollableHeight>1)throw new InvalidOperationException("正常大小的筛选面板仍需滚动。");
                cases.Add(new{width,height,actualHeight=FilterPanel.ActualHeight,scrollable=FilterFieldsScroll.ScrollableHeight,actionsVisible=true});
            }
            FilterPanel.Width=520;FilterPanel.MaxHeight=620;Shell.UpdateLayout();
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(FilterPanel);
            using var file=File.Create(Path.Combine(dataDirectory,"filter-panel.png"));using var stream=file.AsRandomAccessStream();
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
            report["layouts"]=cases;report["status"]="PASS";
        }
        finally{Shell.Children.Remove(FilterPanel);FilterFlyout.Content=FilterPanel;ArrangeFilterPanel();}
    }
}
