using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed record CollectionNode(string Id,string Name,long Count):INavigationNodePresentation
    {
        public string NavigationLabel=>$"{Name} ({Count:N0})";
        public string NavigationGlyph=>"\uE734";
        public override string ToString()=>NavigationLabel;
    }
    private TreeViewNode? collectionsTreeRoot;
    private IReadOnlyList<FileCollection> fileCollections=[];
    private string? activeCollectionId;
    private string[] includedCollectionIds=[],excludedCollectionIds=[];
    private void EnsureCollectionsTreeRoot()
    {
        if(collectionsTreeRoot is null){collectionsTreeRoot=new(){Content=new NavigationGroup("收藏夹"),IsExpanded=true};FolderTree.RootNodes.Insert(0,collectionsTreeRoot);}
    }
    private async Task RefreshCollectionsTree()
    {
        if(catalog is null)return;
        fileCollections=await catalog.ReadCollections(lifetime.Token);if(closing)return;
        EnsureCollectionsTreeRoot();
        collectionsTreeRoot!.Children.Clear();
        foreach(var collection in fileCollections)
        {
            var node=new TreeViewNode{Content=new CollectionNode(collection.Id,collection.Name,collection.Count)};collectionsTreeRoot.Children.Add(node);
            if(collection.Id==activeCollectionId)FolderTree.SelectedNode=node;
        }
        UpdateCollectionFilterLabel();
    }
    private string CollectionLabel(string id)=>fileCollections.FirstOrDefault(c=>c.Id==id)?.Name??"已删除的收藏夹";
    private static StackPanel CollectionSelectionPanel(ListView choices,TextBox name,TextBlock error,long count)
    {
        choices.Height=Math.Min(240,Math.Max(96,choices.Items.Count*44+8));
        var panel=new StackPanel{Spacing=12};panel.Children.Add(new TextBlock{Text=$"已选 {count:N0} 项，可加入多个收藏夹。",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(choices);panel.Children.Add(name);panel.Children.Add(error);return panel;
    }
    private void UpdateCollectionFilterLabel()=>CollectionFilterButton.Content=includedCollectionIds.Length+excludedCollectionIds.Length==0?"收藏标签：不限":$"收藏标签：包含 {includedCollectionIds.Length}，排除 {excludedCollectionIds.Length}";
    private async Task OpenCollection(string id)
    {
        if(!fileCollections.Any(c=>c.Id==id))throw new InvalidOperationException("收藏夹已不存在。");
        await RestoreSavedView(new SavedView("collection:"+id,new FilterSpec{RootId="collection:"+id,CollectionId=id,Kinds=[],ShowHidden=true},null,0,false));
    }
    private async void ManageCollections(object sender,RoutedEventArgs args)
    {
        using var work=browserWork.Enter();if(work is null||closing)return;
        try
        {
            await RefreshCollectionsTree();
            var list=new ListView{ItemsSource=fileCollections,DisplayMemberPath="Name",SelectionMode=ListViewSelectionMode.Single,Height=240,MinWidth=380};
            var name=new TextBox{Header="收藏夹名称",MaxLength=100,PlaceholderText="例如：家具、结案"};
            var error=new TextBlock{TextWrapping=TextWrapping.Wrap};var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            var create=new Button{Content="新建"};var rename=new Button{Content="重命名",IsEnabled=false};var remove=new Button{Content="删除收藏夹",IsEnabled=false};buttons.Children.Add(create);buttons.Children.Add(rename);buttons.Children.Add(remove);
            var panel=new StackPanel{Spacing=10};panel.Children.Add(list);panel.Children.Add(name);panel.Children.Add(buttons);panel.Children.Add(error);
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="收藏夹",Content=panel,PrimaryButtonText="打开所选",CloseButtonText="关闭",IsPrimaryButtonEnabled=false};
            list.SelectionChanged+=(_,_)=>{var c=list.SelectedItem as FileCollection;name.Text=c?.Name??"";rename.IsEnabled=remove.IsEnabled=dialog.IsPrimaryButtonEnabled=c is not null;remove.Content="删除收藏夹";};
            async Task Reload(string? select=null){await RefreshCollectionsTree();list.ItemsSource=fileCollections;list.SelectedItem=fileCollections.FirstOrDefault(c=>c.Id==select);}
            create.Click+=async(_,_)=>{using var submission=browserWork.Enter();if(submission is null||closing)return;try{var created=await catalog!.CreateCollection(name.Text,lifetime.Token);await Reload(created.Id);error.Text="";}catch(Exception ex){error.Text=ex.Message;}};
            rename.Click+=async(_,_)=>{using var submission=browserWork.Enter();if(submission is null||closing||list.SelectedItem is not FileCollection c)return;try{await catalog!.RenameCollection(c.Id,name.Text,lifetime.Token);await Reload(c.Id);error.Text="";}catch(Exception ex){error.Text=ex.Message;}};
            remove.Click+=async(_,_)=>
            {
                using var submission=browserWork.Enter();if(submission is null||closing||list.SelectedItem is not FileCollection c)return;
                if((string)remove.Content!="确认删除"){remove.Content="确认删除";error.Text="仅删除收藏夹及其收藏归属，原文件保留。再次点击确认。";return;}
                try{await DeleteCollectionAndRefresh(c.Id);await Reload();error.Text="收藏夹已删除，原文件保留。";}catch(Exception ex){error.Text=ex.Message;}
            };
            if(await ShowCollectionDialog(dialog)==ContentDialogResult.Primary&&list.SelectedItem is FileCollection chosen)await OpenCollection(chosen.Id);
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private async void CollectSelected(object sender,RoutedEventArgs args)=>await CollectFiles();
    private async Task CollectFiles(FileRow? quickRow=null)
    {
        using var work=browserWork.Enter();if(work is null||closing||catalog is not {} store)return;
        var handle=quickRow is null?resultHandle:null;IReadOnlyList<OrdinalRange> ranges=quickRow is null?SelectedOrdinals(ActiveBrowser):[];
        var first=quickRow?.Item is {} quickItem?new[]{quickItem.EntryId}:handle is null?ActiveBrowser.SelectedItems.OfType<FileRow>().Where(r=>r.Item is not null).Select(r=>r.Item!.EntryId).ToArray():[];
        if(ranges.Count==0&&first.Length==0){Status.Text="请先选择要收藏的文件。";return;}
        bool retained=false;
        try
        {
            if(handle is not null){retained=await store.RetainSnapshot(handle.Id,lifetime.Token);if(!retained)throw new IOException("当前结果已关闭。");}
            await RefreshCollectionsTree();
            var choices=new ListView{ItemsSource=fileCollections,DisplayMemberPath="Name",SelectionMode=ListViewSelectionMode.Multiple,MaxHeight=240,MinWidth=380};
            var name=new TextBox{Header="或新建收藏夹",MaxLength=100,PlaceholderText="输入收藏夹名称"};var error=new TextBlock{TextWrapping=TextWrapping.Wrap};
            var panel=CollectionSelectionPanel(choices,name,error,ranges.Count>0?ranges.Sum(r=>r.Count):first.Length);
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="收藏所选文件",Content=panel,PrimaryButtonText="加入收藏夹",SecondaryButtonText="从所选收藏夹移除",CloseButtonText="取消"};
            async Task<bool> Change(bool add)
            {
                using var submission=browserWork.Enter();if(submission is null||closing)return false;
                dialog.IsPrimaryButtonEnabled=dialog.IsSecondaryButtonEnabled=false;
                try
                {
                    var ids=choices.SelectedItems.Cast<FileCollection>().Select(c=>c.Id).ToList();
                    if(add&&!string.IsNullOrWhiteSpace(name.Text)){var created=await store.CreateCollection(name.Text,lifetime.Token);ids.Add(created.Id);name.Text="";await RefreshCollectionsTree();choices.ItemsSource=fileCollections;foreach(var c in fileCollections.Where(c=>ids.Contains(c.Id)))choices.SelectedItems.Add(c);}
                    if(ids.Count==0)throw new ArgumentException("请选择收藏夹，或填写新收藏夹名称。");
                    using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);stop.CancelAfter(TimeSpan.FromSeconds(30));
                    int changed=handle is null?await store.ChangeCollectionMembers(ids,first,add,stop.Token):await store.ChangeCollectionSelection(ids.ToArray(),handle.Id,ranges,add,stop.Token);
                    if(add){lastCollectionTargets=ids.ToArray();if(quickRow is not null)quickCollectionUsed=true;}
                    RefreshCollectionBadges();
                    await RefreshCollectionsTree();Status.Text=add?$"已添加 {changed:N0} 条收藏归属。":$"已移除 {changed:N0} 条收藏归属，原文件保留。";
                    if(activeCollectionId is not null||includedCollectionIds.Length+excludedCollectionIds.Length>0)await RefreshQuery(preserveViewport:true);
                    return true;
                }
                catch(OperationCanceledException){error.Text="收藏操作已取消，未提交的修改已撤销。";}
                catch(Exception ex){error.Text=ex.Message;}
                finally{dialog.IsPrimaryButtonEnabled=dialog.IsSecondaryButtonEnabled=true;}
                return false;
            }
            async Task Click(bool add,ContentDialogButtonClickEventArgs e){e.Cancel=true;var deferral=e.GetDeferral();try{e.Cancel=!await Change(add);}finally{deferral.Complete();}}
            dialog.PrimaryButtonClick+=async(_,e)=>await Click(true,e);dialog.SecondaryButtonClick+=async(_,e)=>await Click(false,e);
            if(verifyCollectionDialog is not null)await verifyCollectionDialog(dialog,Change);else await ShowCollectionDialog(dialog);
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
        finally{if(retained)await store.ReleaseSnapshot(handle!.Id);}
    }
    private async void ConfigureCollectionFilter(object sender,RoutedEventArgs args)
    {
        using var work=browserWork.Enter();if(work is null||closing)return;
        FilterFlyout.Hide();
        try
        {
            await RefreshCollectionsTree();
            var include=new ListView{ItemsSource=fileCollections,DisplayMemberPath="Name",SelectionMode=ListViewSelectionMode.Multiple,Height=230,MinWidth=220};
            var exclude=new ListView{ItemsSource=fileCollections,DisplayMemberPath="Name",SelectionMode=ListViewSelectionMode.Multiple,Height=230,MinWidth=220};
            foreach(var c in fileCollections){if(includedCollectionIds.Contains(c.Id))include.SelectedItems.Add(c);if(excludedCollectionIds.Contains(c.Id))exclude.SelectedItems.Add(c);}
            var grid=new Grid{ColumnSpacing=16};grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());
            var left=new StackPanel{Spacing=8};left.Children.Add(new TextBlock{Text="包含任一所选收藏夹"});left.Children.Add(include);
            var right=new StackPanel{Spacing=8};right.Children.Add(new TextBlock{Text="排除任一所选收藏夹"});right.Children.Add(exclude);Grid.SetColumn(right,1);grid.Children.Add(left);grid.Children.Add(right);
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="按收藏标签筛选",Content=grid,PrimaryButtonText="应用",SecondaryButtonText="清除收藏筛选",CloseButtonText="取消"};
            var answer=await ShowCollectionDialog(dialog);if(answer==ContentDialogResult.None)return;
            includedCollectionIds=answer==ContentDialogResult.Primary?include.SelectedItems.Cast<FileCollection>().Select(c=>c.Id).ToArray():[];
            excludedCollectionIds=answer==ContentDialogResult.Primary?exclude.SelectedItems.Cast<FileCollection>().Select(c=>c.Id).ToArray():[];
            UpdateCollectionFilterLabel();await ApplyBrowserFilters();
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private async Task DeleteCollectionAndRefresh(string id)
    {
        using var work=browserWork.Enter();if(work is null||closing||catalog is null)return;
        await catalog.DeleteCollection(id,lifetime.Token);
        bool affected=activeCollectionId==id||includedCollectionIds.Contains(id)||excludedCollectionIds.Contains(id);
        includedCollectionIds=includedCollectionIds.Where(value=>value!=id).ToArray();
        excludedCollectionIds=excludedCollectionIds.Where(value=>value!=id).ToArray();
        RefreshCollectionBadges();await RefreshCollectionsTree();
        if(affected)await RefreshQuery(preserveViewport:true);
    }
    private async Task<ContentDialogResult> ShowCollectionDialog(ContentDialog dialog)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        // Async database work must not bring a dialog into another application's
        // active input session. Present it when our window next gains foreground.
        if(!WindowFocus.IsForeground(this))
        {
            var foreground=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ActivatedAgain(object sender,WindowActivatedEventArgs args){if(WindowFocus.IsForeground(this))foreground.TrySetResult();}
            Activated+=ActivatedAgain;
            try{if(WindowFocus.IsForeground(this))foreground.TrySetResult();await foreground.Task.WaitAsync(lifetime.Token);}
            finally{Activated-=ActivatedAgain;}
        }
        lifetime.Token.ThrowIfCancellationRequested();
        using var cancel=lifetime.Token.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog.Hide()));
        return await dialog.ShowAsync();
    }
}
