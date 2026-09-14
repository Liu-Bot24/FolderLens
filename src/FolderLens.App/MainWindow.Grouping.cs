using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System.Collections.ObjectModel;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private FolderGroupingSpec folderGrouping=new();
    private BrowserCollectionView? groupedBrowserSource;
    private ObservableCollection<BrowserFileGroup>? browserGroups;
    private VirtualRangeCollection<FileRow>? flatBrowserItems;
    private void UpdateGroupingButton()
    {
        GroupingButton.Content=folderGrouping.Enabled?$"文件夹分组：{(folderGrouping.Levels=="all"?"全部层级":"仅下一级")} ▾":"文件夹分组：未启用 ▾";
        ToolTipService.SetToolTip(GroupingButton,folderGrouping.Enabled?"先按文件夹顺序，再按组内文件顺序浏览；点击修改或关闭分组":"开启文件夹分组，分别设置文件夹顺序和组内文件顺序");
        ToolTipService.SetToolTip(SortField,folderGrouping.Enabled?"组内文件排序":"全部文件排序");
    }
    private void ToggleFolderGroup(object sender,RoutedEventArgs args)
    {
        if(sender is FrameworkElement{DataContext:BrowserFileGroup group})ToggleFolderGroup(group);
    }
    private void FolderGroupContext(object sender,Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs args)
    {
        if(sender is not FrameworkElement{DataContext:BrowserFileGroup group} element)return;
        string path=group.Info.RelativePath;long revision=rootChangeVersion;
        var menu=new MenuFlyout();var hide=new MenuFlyoutItem{Text="隐藏此文件夹及子文件夹",IsEnabled=path.Length>0};
        hide.Click+=async(_,_)=>
        {
            try
            {
                if(revision!=rootChangeVersion||closing)return;
                ApplyDirectoryHideRule(path);
                await RefreshQuery(preserveViewport:true);
            }
            catch(Exception ex){ShowError(ex);}
        };
        menu.Items.Add(hide);var edit=new MenuFlyoutItem{Text="文件夹筛选…"};edit.Click+=AdvancedFilters;menu.Items.Add(edit);
        menu.ShowAt(element,new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions{Position=args.GetPosition(element)});args.Handled=true;
    }
    private void ApplyDirectoryHideRule(string path)
    {
        var filter=CurrentFilter();var rule=new DirectoryRule("exclude","path","equals",path);
        var candidate=filter with{DirectoryRules=filter.DirectoryRules.Where(r=>r!=rule).Append(rule).ToArray()};
        candidate.Validate();advanced=candidate;
    }
    private void ToggleFolderGroup(BrowserFileGroup group)
    {
        if(browserGroups is null)return;int index=browserGroups.IndexOf(group);if(index<0)return;
        var retained=ActiveBrowser.SelectedItem;double offset=FindScrollViewer(ActiveBrowser)?.VerticalOffset??0;
        bool ownsPublicationRows=publicationRows is null;publicationRows??=visible.ToHashSet();
        bool previous=syncingBrowserSelection;syncingBrowserSelection=true;
        try
        {
            // WinUI's flattened view can crash removing the final nonempty group
            // beside empty groups. Rebuild only the view projection on this explicit
            // user action; the snapshot, lazy rows and scan remain unchanged.
            AttachBrowserView(null);if(groupedBrowserSource is not null)groupedBrowserSource.Source=null;
            groupedBrowserSource=null;group.ToggleCollapsed();
            groupedBrowserSource=new BrowserCollectionView(browserGroups);
            AttachBrowserView(groupedBrowserSource.View);
            ActiveBrowser.SelectedItem=retained is not null&&ActiveBrowser.Items.Contains(retained)?retained:null;
            ActiveBrowser.UpdateLayout();FindScrollViewer(ActiveBrowser)?.ChangeView(null,offset,null,true);
        }
        finally{syncingBrowserSelection=previous;if(ownsPublicationRows)ReleasePublicationRows();UpdateBrowserEmptyState();}
    }
    private void RevealBrowserRow(FileRow row)
    {
        if(browserGroups?.FirstOrDefault(group=>group.IsCollapsed&&row.Ordinal>=group.Info.Start&&row.Ordinal<group.Info.Start+group.Info.Count) is {} hidden)
            ToggleFolderGroup(hidden);
    }
    private async void ConfigureGrouping(object sender,RoutedEventArgs args)
    {
        try
        {
            var enabled=new CheckBox{Content="按文件夹分组",IsChecked=folderGrouping.Enabled};
            ComboBox Choice(string label,(string,string)[] values,string selected)
            {
                var box=new ComboBox{Header=label,HorizontalAlignment=HorizontalAlignment.Stretch};
                foreach(var (text,value) in values)box.Items.Add(new ComboBoxItem{Content=text,Tag=value});SelectTag(box,selected);return box;
            }
            var levels=Choice("分组层级",[("全部层级：保留目录层级，逐层分组","all"),("仅下一级：每组汇集所有深层文件","first")],folderGrouping.Levels);
            var field=Choice("文件夹排序",[("容量","logicalBytes"),("名称","name"),("匹配文件数","matchCount")],folderGrouping.Field);
            var direction=Choice("文件夹顺序",[("↓ 降序：从高到低","desc"),("↑ 升序：从低到高","asc")],folderGrouping.Direction);
            var scope=Choice("用于排名的容量",[("整个已扫描目录（所有文件类型）","all"),("当前筛选匹配文件","matches")],folderGrouping.CapacityScope);
            var options=new StackPanel{Spacing=12};options.Children.Add(levels);options.Children.Add(field);options.Children.Add(direction);options.Children.Add(scope);
            void SetEnabled(){foreach(var box in new[]{levels,field,direction,scope})box.IsEnabled=enabled.IsChecked==true;}
            SetEnabled();enabled.Checked+=(_,_)=>SetEnabled();enabled.Unchecked+=(_,_)=>SetEnabled();
            var panel=new StackPanel{Spacing=16,Width=430};panel.Children.Add(enabled);panel.Children.Add(options);
            panel.Children.Add(new TextBlock{Text="组内文件继续使用主窗口的排序选项。图片始终穿透显示；根目录直接文件单列一组。",TextWrapping=TextWrapping.Wrap});
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="文件夹分组",Content=panel,PrimaryButtonText="应用",CloseButtonText="取消"};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            folderGrouping=new(enabled.IsChecked==true,Tag(levels),Tag(field),Tag(direction),Tag(scope));folderGrouping.Validate();UpdateGroupingButton();await RefreshQuery(preserveViewport:true);
        }
        catch(Exception error){ShowError(error);}
    }
    private void BindBrowserResults(VirtualResults source,IReadOnlyList<SnapshotGroup> groups)
    {
        groupedBrowserSource?.Dispose();
        if(groups.Count==0){groupedBrowserSource=null;browserGroups=null;flatBrowserItems=new(source.Count,index=>(FileRow)source[index]!,source.IndexOf);AttachBrowserView(flatBrowserItems);return;}
        long end=0;foreach(var group in groups){if(group.Start!=end||group.Count<=0)throw new InvalidDataException("文件夹分组区间不连续。");end+=group.Count;}
        if(end!=source.Count)throw new InvalidDataException("文件夹分组未覆盖完整结果。");
        flatBrowserItems=null;browserGroups=new(groups.Select(group=>new BrowserFileGroup(source,group)));
        groupedBrowserSource=new BrowserCollectionView(browserGroups);
        AttachBrowserView(groupedBrowserSource.View);
    }

    private void UpdateBrowserResults(VirtualResults source,IReadOnlyList<SnapshotGroup> groups,IReadOnlyDictionary<string,IReadOnlyList<RangeEdit>> changes,Dictionary<string,double>? diagnostics=null)
    {
        if(browserGroups is null)
        {
            flatBrowserItems!.UpdateRanges(changes[""],index=>(FileRow)source[index]!,source.IndexOf);return;
        }
        var wanted=groups.Select(group=>group.Id).ToHashSet();
        for(int i=browserGroups.Count-1;i>=0;i--)if(!wanted.Contains(browserGroups[i].Info.Id))browserGroups.RemoveAt(i);
        var existing=browserGroups.Select((group,index)=>(group,index)).ToDictionary(pair=>pair.group.Info.Id);
        var remaining=new RemainingOrder(browserGroups.Count);
        long started=0,allocated=0;
        void Begin(){if(diagnostics is null)return;started=System.Diagnostics.Stopwatch.GetTimestamp();allocated=GC.GetAllocatedBytesForCurrentThread();}
        void End(string label,long count){if(diagnostics is null)return;diagnostics[label+"Ms"]=diagnostics.GetValueOrDefault(label+"Ms")+System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;diagnostics[label+"Bytes"]=diagnostics.GetValueOrDefault(label+"Bytes")+GC.GetAllocatedBytesForCurrentThread()-allocated;diagnostics[label+"Rows"]=diagnostics.GetValueOrDefault(label+"Rows")+count;}
        for(int index=0;index<groups.Count;index++)
        {
            var info=groups[index];
            if(existing.TryGetValue(info.Id,out var entry))
            {
                var group=entry.group;int current=index+remaining.Take(entry.index);
                // WinUI's INCC adapter maps Move to Reset. Removing/inserting the
                // same group preserves unaffected groups instead of recycling all rows.
                if(current!=index){Begin();browserGroups.RemoveAt(current);browserGroups.Insert(index,group);End("groupMove",group.Info.Count);}
                Begin();
                group.Update(source,info,changes[info.Id]);
                End("groupUpdate",changes[info.Id].Sum(edit=>(long)edit.Added+edit.Removed));
            }
            else{Begin();browserGroups.Insert(index,new(source,info));End("groupAdd",info.Count);}
        }
    }

    private static IReadOnlyDictionary<string,IReadOnlyList<RangeEdit>> PreserveVisibleRanges(IReadOnlyList<FileRow> rows,IReadOnlyList<SnapshotGroup> oldGroups,IReadOnlyList<SnapshotGroup> newGroups,IReadOnlyDictionary<string,SnapshotSplice> edits,IReadOnlyList<SnapshotItem> matches)
    {
        var oldStarts=oldGroups.ToDictionary(group=>group.Id,group=>group.Start);var newStarts=newGroups.ToDictionary(group=>group.Id,group=>group.Start);
        var matched=matches.ToDictionary(item=>item.EntryId);var anchors=new Dictionary<string,List<(int Old,int New)>>();
        foreach(var row in rows)
        {
            if(row.Item is not {} old||!matched.TryGetValue(old.EntryId,out var item)||old.Version!=item.Version||old.RelativePath!=item.RelativePath||old.Bytes!=item.Bytes||old.Allocated!=item.Allocated||old.Kind!=item.Kind)continue;
            string id=oldGroups.Count==0?"":old.Group?.Id??"";
            if(id!=(newGroups.Count==0?"":item.Group?.Id??"")||!edits.ContainsKey(id))continue;
            if(!anchors.TryGetValue(id,out var list))anchors[id]=list=[];
            list.Add((checked((int)(row.Ordinal-(oldStarts.GetValueOrDefault(id)))),checked((int)(item.Ordinal-newStarts.GetValueOrDefault(id)))));
        }
        return edits.ToDictionary(pair=>pair.Key,pair=>pair.Value.Preserve(anchors.GetValueOrDefault(pair.Key)??[]));
    }

    private void RetainBrowserRows(VirtualResults previous,VirtualResults next,IReadOnlyList<FileRow> rows,IReadOnlyList<SnapshotGroup> previousGroups,IReadOnlyList<SnapshotGroup> nextGroups,IReadOnlyDictionary<string,SnapshotSplice> edits,IReadOnlyList<SnapshotItem> matches)
    {
        var matched=matches.ToDictionary(item=>item.EntryId);var newGroups=nextGroups.ToDictionary(group=>group.Id);
        foreach(var row in rows)
        {
            long ordinal=previous.IndexOf(row);
            if(ordinal<0)continue;
            SnapshotGroup? oldGroup=null,newGroup=null;
            if(previousGroups.Count>0)
            {
                int low=0,high=previousGroups.Count-1;
                while(low<=high){int mid=(low+high)/2;var candidate=previousGroups[mid];if(ordinal<candidate.Start)high=mid-1;else if(ordinal>=candidate.Start+candidate.Count)low=mid+1;else{oldGroup=candidate;break;}}
                if(oldGroup is not null)newGroups.TryGetValue(oldGroup.Id,out newGroup);
            }
            string id=oldGroup?.Id??"";
            if((oldGroup is null||newGroup is not null)&&edits.TryGetValue(id,out var edit)&&edit.MapOldIndex(checked((int)(ordinal-(oldGroup?.Start??0)))) is {} mapped)
                next.Retain(row,(newGroup?.Start??0)+mapped,newGroup);
            else if(row.Item is {} old&&matched.TryGetValue(old.EntryId,out var item)&&old.Version==item.Version&&old.RelativePath==item.RelativePath&&old.Bytes==item.Bytes&&old.Allocated==item.Allocated&&old.Kind==item.Kind)
                next.Retain(row,item);
        }
    }
}
