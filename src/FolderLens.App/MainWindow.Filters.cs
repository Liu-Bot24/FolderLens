using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private ContentDialog? advancedFilterDialog;
    private void ShowActiveFilters(FilterSpec filter)
    {
        var parts=new List<string>();
        if(filter.CollectionId is {} collection)parts.Add("收藏夹："+CollectionLabel(collection));
        if(filter.IncludeCollections.Length>0)parts.Add("包含收藏："+string.Join("、",filter.IncludeCollections.Select(CollectionLabel)));
        if(filter.ExcludeCollections.Length>0)parts.Add("排除收藏："+string.Join("、",filter.ExcludeCollections.Select(CollectionLabel)));
        Search.PlaceholderText=filter.SearchScope=="nameAndPath"?"搜索文件名或相对路径…":"搜索文件名…";
        if(!string.IsNullOrWhiteSpace(filter.NamePathQuery))parts.Add((filter.SearchScope=="nameAndPath"?"文件名或路径：":"文件名：")+filter.NamePathQuery);
        foreach(var range in filter.Ranges)
        {
            string label=range.Key switch{"logicalBytes"=>"大小","allocatedBytes"=>"占用空间","width"=>"宽度","height"=>"高度","longEdge"=>"长边","shortEdge"=>"短边","pixelCount"=>"像素数","durationMs"=>"时长（毫秒）",_=>range.Key};
            string Format(long value)=>range.Key is "logicalBytes" or "allocatedBytes"?FileRow.FormatBytes(value):value.ToString("N0");
            if(range.Value.Min is {} minimum)parts.Add($"{label} ≥ {Format(minimum)}");
            if(range.Value.Max is {} maximum)parts.Add($"{label} ≤ {Format(maximum)}");
        }
        if(filter.Formats.Length>0)parts.Add("格式："+string.Join("、",filter.Formats));
        if(filter.FileExtensions.Length>0)parts.Add("扩展名："+string.Join("、",filter.FileExtensions.Select(value=>new ExtensionOption(value).Label)));
        if(filter.Raw!="any")parts.Add(filter.Raw=="only"?"仅 RAW":"排除 RAW");
        if(filter.Animation!="any")parts.Add(filter.Animation=="animated"?"仅动图":"仅静态图");
        if(!filter.Recursive)parts.Add("不穿透子目录");
        if(filter.Exclusions.Length>0)parts.Add($"排除 {filter.Exclusions.Length} 个目录规则");
        if(filter.DirectoryRules.Any(r=>r.Enabled))parts.Add($"文件夹筛选：{filter.DirectoryRules.Count(r=>r.Enabled)} 条规则");
        foreach(var date in filter.Dates)
            parts.Add(FolderLens.Core.DateRangeDisplay.Format(date));
        if(filter.AspectRatio is not null)parts.Add("已限制宽高比");
        if(filter.Orientation!="any")parts.Add("已限制图片方向");
        if(filter.FrameRate is not null||filter.VideoCodecs.Length>0||filter.AudioCodecs.Length>0)parts.Add("已限制媒体编码或帧率");
        ActiveFilterSummary.Text="生效筛选："+string.Join(" · ",parts);
        ActiveFilterSummary.Visibility=parts.Count>0?Visibility.Visible:Visibility.Collapsed;
    }
    private async void FillMetadata(object sender,RoutedEventArgs e)
    {
        try{await StartMetadataRefresh();await RefreshQuery();}
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private bool metadataRefreshPending;
    private Task StartMetadataRefresh()
    {
        if(closing||scanStop.IsCancellationRequested)return Task.CompletedTask;
        metadataRefreshPending=true;
        return metadataTask is {IsCompleted:false}?metadataTask:metadataTask=DrainMetadataRefresh();
    }
    private async Task DrainMetadataRefresh()
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        long revision=rootChangeVersion;var token=scanStop.Token;
        while(metadataRefreshPending&&!closing&&!token.IsCancellationRequested&&revision==rootChangeVersion)
        {
            metadataRefreshPending=false;await FillCurrentMetadata();
        }
    }
    private async Task FillCurrentMetadata()
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        if(catalog is null||metadataWorker is null||media is null||rootId.Length==0)return;string activeId=rootId;long revision=rootChangeVersion;var token=scanStop.Token;
        bool Current()=>activeId==rootId&&revision==rootChangeVersion&&!closing&&!token.IsCancellationRequested;
        try
        {
            if(verifyVideoMetadataBarrier is not null)await verifyVideoMetadataBarrier(token);
            await new FolderLens.Infrastructure.MetadataPump(catalog,metadataWorker,media).FillAll(activeId,root,epoch,new Progress<long>(count=>{if(Current())ReportMetadataProgress(count);}),token,activeCollectionId);
            if(Current())
            {
                // Cover readiness does not imply that catalog metadata was ready when
                // that cover loaded. Refresh properties without replacing its bitmap.
                await Task.WhenAll(visible.Where(row=>row.Kind=="video").ToArray().Select(row=>LoadRowProperties(row,refresh:true)));
                if(!Current())return;
                if(browserScanError is null)Status.Text="文件信息已补充，当前浏览顺序保持不变。";
                if(!BrowserSequenceLocked&&!restoringView)await RefreshQuery(preserveViewport:true,scanPreview:true);
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private void ReportMetadataProgress(long count)
    {
        if(count%32==0)Status.Text=$"已补充 {count:N0} 个文件的信息。";
    }
    private async void AdvancedFilters(object sender,RoutedEventArgs e)
    {
        if(rootId.Length==0||closing)return;
        try
        {
            long revision=rootChangeVersion;string sourceRoot=root;string? sourceCollection=activeCollectionId;
            var editor=new AdvancedFilterEditor(CurrentFilter());
            using var dialogStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var rules=CurrentFilter().Exclusions.Where(r=>r.Mode=="skipScan").ToList();
            var legacyRules=CurrentFilter().Exclusions.Where(r=>r.Mode=="hideView").ToList();
            async Task<string?> PickDirectory()
            {
                var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder=await picker.PickSingleFolderAsync();if(folder is null)return null;
                if(sourceCollection is not null)return await catalog!.RelativeCollectionDirectory(sourceCollection,folder.Path,dialogStop.Token);
                string relative=Path.GetRelativePath(sourceRoot,folder.Path);
                if(Path.IsPathRooted(relative)||relative=="."||relative.Split('\\','/').Contains(".."))throw new ArgumentException("请选择当前根目录内的子文件夹。");
                return relative;
            }
            var exclusions=editor.Section("文件夹筛选",true);
            var folderSection=editor.View.Children.Last();editor.View.Children.Remove(folderSection);editor.View.Children.Insert(0,folderSection);
            var previewCatalog=catalog!;string previewRootId=rootId;
            var directoryEditor=new DirectoryRuleEditor(CurrentFilter().DirectoryRules,PickDirectory,
                (draft,token)=>previewCatalog.PreviewDirectoryRules(previewRootId,draft,token,sourceCollection),dialogStop.Token);
            exclusions.Children.Add(directoryEditor.View);
            if(legacyRules.Count>0)
            {
                var legacySection=editor.Section("已保存的路径排除",false);
                legacySection.Children.Add(new TextBlock{Text="这些路径继续生效。选择一条路径并点击删除即可取消排除。上方预览不包含这里的路径排除。",TextWrapping=TextWrapping.Wrap});
                var legacyList=new ListView{MaxHeight=180,ItemsSource=legacyRules.Select(r=>r.RelativePath).ToArray()};legacySection.Children.Add(legacyList);
                var removeLegacy=new Button{Content="删除选中路径",IsEnabled=false};
                legacyList.SelectionChanged+=(_,_)=>removeLegacy.IsEnabled=legacyList.SelectedIndex>=0;
                removeLegacy.Click+=(_,_)=>{if(legacyList.SelectedIndex is var i&&i>=0){legacyRules.RemoveAt(i);legacyList.ItemsSource=legacyRules.Select(r=>r.RelativePath).ToArray();}};
                legacySection.Children.Add(removeLegacy);
            }
            var skipSection=editor.Section("不扫描的文件夹（高级）",rules.Count>0);
            if(sourceCollection is not null)editor.View.Children.Last().Visibility=Visibility.Collapsed;
            skipSection.Children.Add(new TextBlock{Text="这些文件夹不进入索引，例外保留不能恢复其内容。修改后需要重新扫描。通常请使用上面的文件夹筛选。",TextWrapping=TextWrapping.Wrap});
            var list=new ListView{MaxHeight=144};
            void UpdateRules()=>list.ItemsSource=rules.Select(r=>r.RelativePath).ToArray();
            UpdateRules();skipSection.Children.Add(list);
            var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
            error.Foreground=(Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            var skip=new Button{Content="添加不扫描的文件夹…"};
            skip.Click+=async(_,_)=>{try{string? path=await PickDirectory();if(path is null)return;if(!rules.Any(r=>r.RelativePath==path))rules.Add(new(path,"skipScan"));UpdateRules();}catch(Exception ex){error.Text=ex.Message;error.Visibility=Visibility.Visible;}};
            var remove=new Button{Content="删除选中规则",IsEnabled=false};list.SelectionChanged+=(_,_)=>remove.IsEnabled=list.SelectedIndex>=0;
            remove.Click+=(_,_)=>{if(list.SelectedIndex is var i&&i>=0){rules.RemoveAt(i);UpdateRules();}};
            skipSection.Children.Add(skip);skipSection.Children.Add(remove);
            var body=new StackPanel{Spacing=12,Width=Math.Max(240,Math.Min(520,Shell.XamlRoot.Size.Width-112))};
            body.Children.Add(new ScrollViewer{Content=editor.View,MaxHeight=Math.Max(160,Math.Min(580,Shell.XamlRoot.Size.Height-220)),HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});body.Children.Add(error);
            FilterSpec? candidate=null;
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="更多筛选",Content=body,PrimaryButtonText="应用筛选",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary};
            dialog.Resources["ContentDialogMaxWidth"]=640d;
            dialog.PrimaryButtonClick+=(_,args)=>
            {
                try
                {
                    if(closing||revision!=rootChangeVersion)throw new InvalidOperationException("当前目录已变化，请重新打开筛选。");
                    candidate=editor.Read(rules.Concat(legacyRules).ToArray()) with{DirectoryRules=directoryEditor.Read()};candidate.Validate();error.Visibility=Visibility.Collapsed;
                }
                catch(Exception ex){candidate=null;args.Cancel=true;error.Text=ex.Message;error.Visibility=Visibility.Visible;}
            };
            advancedFilterDialog=dialog;
            ContentDialogResult result;
            try{result=await dialog.ShowAsync();}finally{dialogStop.Cancel();advancedFilterDialog=null;}
            if(result!=ContentDialogResult.Primary||candidate is null||closing||revision!=rootChangeVersion)return;
            advanced=candidate;MinWidth.Value=double.NaN;MinHeight.Value=double.NaN;
            await ApplyBrowserFilters();
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
}
