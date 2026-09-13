using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyDirectoryFilter(string source,Dictionary<string,object> report)
    {
        string excluded=Path.Combine(source,"A","仅预览");Directory.CreateDirectory(excluded);
        File.Copy(Path.Combine(source,"A","image-00.png"),Path.Combine(excluded,"cover.png"));
        await File.WriteAllTextAsync(Path.Combine(source,"document.pdf"),"fixture");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        long previousCount=resultHandle!.Count;long beforeEpoch=epoch;
        var legacy=Enumerable.Range(0,65).Select(i=>new ExclusionSpec("legacy-"+i,"hideView"))
            .Concat([new(new string('a',513),"hideView"),new("legacy/A","hideView"),new("legacy/a","hideView")]).ToArray();
        advanced=CurrentFilter() with{Exclusions=legacy};
        AdvancedFilters(this,new RoutedEventArgs());
        await WaitUntil(()=>advancedFilterDialog is {IsLoaded:true},TimeSpan.FromSeconds(3));
        var dialog=advancedFilterDialog!;
        var body=(StackPanel)dialog.Content;
        var editor=(StackPanel)((ScrollViewer)body.Children[0]).Content;
        var section=(Expander)editor.Children[0];
        if(section.Header as string!="文件夹筛选")throw new InvalidOperationException("文件夹筛选没有放在更多条件的首位。");
        var rules=(StackPanel)((StackPanel)section.Content).Children[0];
        var pattern=rules.Children.OfType<TextBox>().Single();pattern.Text="仅预览";
        void Invoke(string label)=>((IInvokeProvider)new ButtonAutomationPeer(rules.Children.OfType<Button>().Single(b=>b.Content as string==label)).GetPattern(PatternInterface.Invoke)).Invoke();
        Invoke("添加规则");await Task.Delay(50);
        var list=rules.Children.OfType<ListView>().Single();
        if(list.Items.Count!=1)throw new InvalidOperationException("点击添加没有创建规则。");
        var enabled=(CheckBox)list.Items[0];enabled.IsChecked=false;enabled.IsChecked=true;
        list.SelectedIndex=0;Invoke("编辑选中规则");pattern.Text="仅预览副本";Invoke("取消编辑");
        list.SelectedIndex=0;Invoke("编辑选中规则");
        if(pattern.Text!="仅预览")throw new InvalidOperationException("取消编辑没有保留原规则。");
        pattern.Text="仅预览";Invoke("保存规则");
        if(list.Items.Count!=1)throw new InvalidOperationException("编辑规则产生了重复规则。");
        Invoke("预览筛选结果");
        await WaitUntil(()=>rules.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("隐藏 1 个文件")),TimeSpan.FromSeconds(5));
        // Apply through the native dialog button, preserving the current scan epoch.
        dialog.ApplyTemplate();dialog.UpdateLayout();
        FrameworkElement? Find(DependencyObject parent,string name)
        {
            if(parent is FrameworkElement element&&element.Name==name)return element;
            for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
                if(Find(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent,i),name) is {} found)return found;
            return null;
        }
        var apply=(Button)(Find(dialog,"PrimaryButton")??throw new InvalidOperationException("未找到应用筛选按钮。"));
        ((IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitUntil(()=>advancedFilterDialog is null&&!queryBusy&&resultHandle!.Count==previousCount-1,TimeSpan.FromSeconds(5));
        if(epoch!=beforeEpoch||advanced?.DirectoryRules.Length!=1)throw new InvalidOperationException("应用文件夹规则重新扫描或丢失规则。");
        if(!advanced.Exclusions.SequenceEqual(legacy))throw new InvalidOperationException("旧版路径排除在编辑器往返时被改写或丢失。");
        var saved=CaptureView();ApplySavedFilter(saved.Filter);
        if(CurrentFilter().DirectoryRules.Length!=1)throw new InvalidOperationException("保存视图丢失文件夹规则。");
        long previousGeneration=generation;SelectTag(Category,"pdf");
        await WaitUntil(()=>generation>previousGeneration&&!queryBusy,TimeSpan.FromSeconds(5));
        if(resultHandle!.Count!=1||DetailsMode.IsChecked!=true)throw new InvalidOperationException("PDF 分类没有使用详细信息显示未解码的 PDF 文件。");
        if(detailColumns.Single(c=>c.Field=="pixelCount").Visible||detailColumns.Single(c=>c.Field=="durationMs").Visible||detailColumns.Single(c=>c.Field=="allocatedBytes").Visible)throw new InvalidOperationException("PDF 默认显示了不适用的详细信息列。");
        var widthColumn=detailColumns.Single(c=>c.Field=="pixelCount");widthColumn.Resize(197);
        await SaveDetailWidths();await RestoreDetailWidths();
        if(widthColumn.SavedWidth!=197||widthColumn.Width.Value!=0)throw new InvalidOperationException("隐藏列丢失列宽或仍占据空间。");
        var pdfRow=new FileRow(0);pdfRow.Fill((await catalog!.ReadFirstPage(CurrentFilter())).Items.Single());
        if(pdfRow.FileIconVisibility!=Visibility.Visible||pdfRow.FileTypeBadge!="PDF")throw new InvalidOperationException("文档没有格式图标。");
        DetailsMode.IsChecked=false;ToggleView(this,new());previousGeneration=generation;SelectTag(Category,"image");
        await WaitUntil(()=>generation>previousGeneration&&!queryBusy,TimeSpan.FromSeconds(5));
        if(!widthColumn.Visible||widthColumn.Width.Value!=197)throw new InvalidOperationException("切到图片分类没有恢复尺寸列及其宽度。");
        previousGeneration=generation;SelectTag(Category,"pdf");await WaitUntil(()=>generation>previousGeneration&&!queryBusy,TimeSpan.FromSeconds(5));
        if(DetailsMode.IsChecked==true)throw new InvalidOperationException("切回分类后覆盖了用户选择的网格模式。");
        FilesGrid.UpdateLayout();await WaitUntil(()=>FilesGrid.ContainerFromIndex(0) is GridViewItem,TimeSpan.FromSeconds(3));
        var card=(GridViewItem)FilesGrid.ContainerFromIndex(0);
        bool VisibleBadge(DependencyObject parent)
        {
            if(parent is FrameworkElement {Visibility:Visibility.Collapsed})return false;
            if(parent is TextBlock {Text:"PDF"})return true;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(VisibleBadge(VisualTreeHelper.GetChild(parent,i)))return true;
            return false;
        }
        await WaitUntil(()=>VisibleBadge(card),TimeSpan.FromSeconds(3));
        var capture=new RenderTargetBitmap();await capture.RenderAsync(card);byte[] pixels=(await capture.GetPixelsAsync()).ToArray();
        using(var file=File.Create(Path.Combine(dataDirectory,"document-card.png")))
        using(var stream=file.AsRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)capture.PixelWidth,(uint)capture.PixelHeight,96,96,pixels);await encoder.FlushAsync();
        }
        report["documentCardVisible"]=true;report["columnVisibilityAndWidth"]=true;report["editSaveCancel"]=true;
        var originalAdvanced=advanced;
        advanced=CurrentFilter() with{DirectoryRules=Enumerable.Range(0,64).Select(i=>new DirectoryRule("exclude","name","equals","rule-"+i)).ToArray()};
        var full=advanced;bool rejected=false;
        try{ApplyDirectoryHideRule("new-folder");}catch(ArgumentException){rejected=true;}
        if(!rejected||!ReferenceEquals(advanced,full))throw new InvalidOperationException("第65条隐藏规则污染了当前筛选。");
        CurrentFilter().Validate();advanced=originalAdvanced;report["invalidHideDoesNotMutateFilter"]=true;
        var prior=advanced;rejected=false;
        try{ApplyDirectoryHideRule(new string('a',513));}catch(ArgumentException){rejected=true;}
        if(!rejected||!ReferenceEquals(prior,advanced))throw new InvalidOperationException("超长隐藏路径污染了当前筛选。");
        var ruleEditor=new DirectoryRuleEditor([new("exclude","name","equals","cache",Enabled:false),new("exclude","name","equals","cache")],()=>Task.FromResult<string?>(null),(_,_)=>throw new NotSupportedException(),lifetime.Token);
        var ruleList=ruleEditor.View.Children.OfType<ListView>().Single();((CheckBox)ruleList.Items[1]).IsChecked=false;
        ruleEditor.View.Children.OfType<TextBox>().Single().Text="another";
        ((IInvokeProvider)new ButtonAutomationPeer(ruleEditor.View.Children.OfType<Button>().Single(b=>b.Content as string=="添加规则")).GetPattern(PatternInterface.Invoke)).Invoke();
        ((CheckBox)ruleList.Items[1]).IsChecked=true;
        if(ruleEditor.Read()[0].Enabled||!ruleEditor.Read()[1].Enabled)throw new InvalidOperationException("重复规则的勾选修改了其他行。");
        DetailsMode.IsChecked=false;ToggleView(DetailsMode,new());ApplySavedFilter(CurrentFilter() with{Kinds=["image"],Extensions=[]});
        if(!categoryDetailViews.TryGetValue("pdf",out bool preference)||preference)throw new InvalidOperationException("恢复收藏前的用户网格偏好未记录。");
        report["nativeAddPausePreviewApply"]=true;report["noRescan"]=true;report["pdfCategory"]=true;report["status"]="PASS";
    }
}
