using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private void ShowActiveFilters(FilterSpec filter)
    {
        var parts=new List<string>();
        foreach(var range in filter.Ranges)
        {
            string label=range.Key switch{"logicalBytes"=>"文件体积","allocatedBytes"=>"磁盘占用","width"=>"宽度","height"=>"高度","longEdge"=>"长边","shortEdge"=>"短边","pixelCount"=>"像素数","durationMs"=>"时长（毫秒）",_=>range.Key};
            string Format(long value)=>range.Key is "logicalBytes" or "allocatedBytes"?FileRow.FormatBytes(value):value.ToString("N0");
            if(range.Value.Min is {} minimum)parts.Add($"{label} ≥ {Format(minimum)}");
            if(range.Value.Max is {} maximum)parts.Add($"{label} ≤ {Format(maximum)}");
        }
        if(filter.Formats.Length>0)parts.Add("格式："+string.Join("、",filter.Formats));
        if(filter.Raw!="any")parts.Add(filter.Raw=="only"?"仅 RAW":"排除 RAW");
        if(filter.Animation!="any")parts.Add(filter.Animation=="animated"?"仅动图":"仅静态图");
        if(!filter.Recursive)parts.Add("不穿透子目录");
        if(filter.Exclusions.Length>0)parts.Add($"排除 {filter.Exclusions.Length} 个目录规则");
        if(filter.Dates.Length>0)parts.Add($"日期范围：{filter.Dates.Length} 项");
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
            await new FolderLens.Infrastructure.MetadataPump(catalog,metadataWorker,media).FillAll(activeId,root,epoch,new Progress<long>(count=>{if(Current()&&count%32==0)Status.Text=$"已补充 {count:N0} 个文件的信息。";}),token);
            if(Current())
            {
                // Cover readiness does not imply that catalog metadata was ready when
                // that cover loaded. Refresh properties without replacing its bitmap.
                await Task.WhenAll(visible.Where(row=>row.Kind=="video").ToArray().Select(row=>LoadRowProperties(row,refresh:true)));
                if(!Current())return;
                Status.Text="文件信息已补充，当前浏览顺序保持不变。";
                if(selected is null&&!restoringView)await RefreshQuery(preserveViewport:true,scanPreview:true);
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private async void AdvancedFilters(object sender,RoutedEventArgs e)
    {
        if(rootId.Length==0||closing)return;
        try
        {
            long revision=rootChangeVersion;string sourceRoot=root;
            var editor=new AdvancedFilterEditor(CurrentFilter());
            var rules=CurrentFilter().Exclusions.ToList();
            var exclusions=editor.Section("目录排除",rules.Count>0);
            var list=new ListView{MaxHeight=144,SelectionMode=ListViewSelectionMode.Single};
            void UpdateRules()=>list.ItemsSource=rules.Select(r=>$"{(r.Mode=="hideView"?"只在视图隐藏":"完全不扫描")} · {r.RelativePath}").ToArray();
            UpdateRules();exclusions.Children.Add(list);
            var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
            error.Foreground=(Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            async Task AddRule(string mode)
            {
                try
                {
                    var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var folder=await picker.PickSingleFolderAsync();if(folder is null)return;
                    string relative=Path.GetRelativePath(sourceRoot,folder.Path);
                    if(Path.IsPathRooted(relative)||relative=="."||relative.Split('\\','/').Contains(".."))throw new ArgumentException("请选择当前根目录内的子文件夹。");
                    if(!rules.Any(rule=>rule.RelativePath==relative&&rule.Mode==mode))rules.Add(new(relative,mode));
                    UpdateRules();error.Visibility=Visibility.Collapsed;
                }
                catch(Exception ex){error.Text=ex.Message;error.Visibility=Visibility.Visible;}
            }
            var actions=new StackPanel{Spacing=8};
            var hide=new Button{Content="添加仅在视图中隐藏的目录",HorizontalAlignment=HorizontalAlignment.Stretch};
            var skip=new Button{Content="添加不扫描的目录",HorizontalAlignment=HorizontalAlignment.Stretch};
            var remove=new Button{Content="移除选中规则",HorizontalAlignment=HorizontalAlignment.Stretch,IsEnabled=false};
            list.SelectionChanged+=(_,_)=>remove.IsEnabled=list.SelectedIndex>=0;
            hide.Click+=async(_,_)=>await AddRule("hideView");skip.Click+=async(_,_)=>await AddRule("skipScan");
            remove.Click+=(_,_)=>{int index=list.SelectedIndex;if(index>=0){rules.RemoveAt(index);UpdateRules();}};
            actions.Children.Add(hide);actions.Children.Add(skip);actions.Children.Add(remove);exclusions.Children.Add(actions);
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
                    candidate=editor.Read(rules.ToArray());error.Visibility=Visibility.Collapsed;
                }
                catch(Exception ex){candidate=null;args.Cancel=true;error.Text=ex.Message;error.Visibility=Visibility.Visible;}
            };
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary||candidate is null||closing||revision!=rootChangeVersion)return;
            advanced=candidate;MinWidth.Value=double.NaN;MinHeight.Value=double.NaN;
            await ApplyBrowserFilters();
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
}
