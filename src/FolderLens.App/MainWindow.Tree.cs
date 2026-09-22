using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private TreeViewNode? activeTreeRoot;
    private TreeViewNode? computerTreeRoot;
    private sealed record NavigationGroup(string Label):INavigationNodePresentation
    {
        public string NavigationLabel=>Label;
        public string NavigationGlyph=>Label=="收藏夹"?"\uE734":"\uE770";
        public override string ToString()=>Label;
    }
    private async Task InitializeNavigationTree()
    {
        // Basic navigation is available even while the catalog is opening or migrating.
        EnsureCollectionsTreeRoot();
        var locations=await Task.Run(NavigationLocations.Read,lifetime.Token);
        if(closing)return;
        foreach(var place in locations.Places)
            FolderTree.RootNodes.Add(new TreeViewNode{Content=new FolderNode(place.Path,place.Label,Icon:place.Label switch{"桌面"=>"\uE977","下载"=>"\uE896","文档"=>"\uE8A5","图片"=>"\uE8B9","音乐"=>"\uE8D6","视频"=>"\uE8B2","用户文件夹"=>"\uE77B",_=>"\uE8B7"}),HasUnrealizedChildren=true});
        computerTreeRoot=new TreeViewNode{Content=new NavigationGroup("此电脑"),IsExpanded=true};
        foreach(string drive in locations.Drives)
            computerTreeRoot.Children.Add(new TreeViewNode{Content=new FolderNode(drive,drive.TrimEnd('\\'),Icon:"\uEDA2"),HasUnrealizedChildren=true});
        FolderTree.RootNodes.Add(computerTreeRoot);
    }
    private Task? treeRefreshTask;
    private bool treeRefreshPending;
    private long nextTreeRefresh;
    private CancellationTokenSource physicalTreeStop=new();
    private Task? physicalTreeTask;
    private sealed record TreeListing(DirectoryListing Listing,long Start,FolderNode Folder);
    private readonly Dictionary<TreeViewNode,TreeListing> treeListings=[];
    private readonly SemaphoreSlim treeListingSlots=new(2,2);
    private readonly Dictionary<TreeViewNode,long> treeListingRequests=[];
    private sealed record TreePageIntent(long Version,long Start);
    private readonly Dictionary<TreeViewNode,TreePageIntent> treePageIntents=[];
    private long treePageVersion;
    private string TreeListingDirectory=>Path.Combine(RuntimeDataDirectory,"temp","tree-listings");

    private void ResetTreeWork()
    {
        physicalTreeStop.Cancel();physicalTreeStop.Dispose();physicalTreeStop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }
    private void ShowTreeRoot(string path)
    {
        ResetTreeWork();
        var existing=new Queue<TreeViewNode>(FolderTree.RootNodes);
        TreeViewNode? closest=null;int closestLength=0;
        while(existing.TryDequeue(out var candidate))
        {
            if(candidate.Content is FolderNode {PageOffset:null} folder&&string.Equals(folder.Path,path,StringComparison.OrdinalIgnoreCase))
            {
                activeTreeRoot=candidate;
                for(var ancestor=candidate;ancestor is not null;ancestor=ancestor.Parent)ancestor.IsExpanded=true;
                FolderTree.SelectedNode=candidate;nextTreeRefresh=0;physicalTreeTask=RefreshAncestors(candidate,rootChangeVersion,physicalTreeStop.Token);return;
            }
            if(candidate.Content is FolderNode {PageOffset:null} ancestorFolder&&ancestorFolder.Path.Length>closestLength&&
                path.StartsWith(ancestorFolder.Path.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
            {closest=candidate;closestLength=ancestorFolder.Path.Length;}
            foreach(var child in candidate.Children)existing.Enqueue(child);
        }
        TreeViewNode? parent=closest;
        var ancestors=new Stack<string>();
        string? stopAt=(closest?.Content as FolderNode)?.Path;
        for(string? current=path;current is not null&&!string.Equals(current,stopAt,StringComparison.OrdinalIgnoreCase);current=Path.GetDirectoryName(current))ancestors.Push(current);
        foreach(string ancestor in ancestors)
        {
            var node=new TreeViewNode{Content=new FolderNode(ancestor,FolderLabel(ancestor)),HasUnrealizedChildren=true};
            if(parent is null)
            {
                if(computerTreeRoot is not null)computerTreeRoot.Children.Add(node);else FolderTree.RootNodes.Add(node);
            }
            else parent.Children.Add(node);
            node.IsExpanded=true;parent=node;
        }
        activeTreeRoot=parent;FolderTree.SelectedNode=parent;
        for(var ancestor=parent;ancestor is not null;ancestor=ancestor.Parent)ancestor.IsExpanded=true;
        nextTreeRefresh=0;
        if(parent is not null)physicalTreeTask=RefreshAncestors(parent,rootChangeVersion,physicalTreeStop.Token);
    }
    private async Task RefreshAncestors(TreeViewNode node,long revision,CancellationToken cancellation)
    {
        try
        {
            for(var ancestor=node.Parent;ancestor is not null;ancestor=ancestor.Parent)
                if(ancestor.IsExpanded)await ReadPhysicalChildren(ancestor,revision,cancellation);
            if(revision!=rootChangeVersion||cancellation.IsCancellationRequested||closing||FolderTree.SelectedNode!=node)return;
            FolderTree.ApplyTemplate();
            // A node outside the viewport has no container. Ask the virtualizing
            // list to realize it before attempting container-based positioning.
            if(FindTreeList(FolderTree) is {} list&&list.Items.Contains(node))list.ScrollIntoView(node,ScrollIntoViewAlignment.Default);
            if(FolderTree.ContainerFromNode(node) is Microsoft.UI.Xaml.FrameworkElement selectedNode)
                selectedNode.StartBringIntoView(new Microsoft.UI.Xaml.BringIntoViewOptions{AnimationDesired=false,VerticalAlignmentRatio=.5});
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(!closing&&revision==rootChangeVersion)ShowError(error);}
    }
    private static ListViewBase? FindTreeList(Microsoft.UI.Xaml.DependencyObject root)
    {
        if(root is ListViewBase list)return list;
        for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindTreeList(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
    private async Task ReadPhysicalChildren(TreeViewNode node,long revision,CancellationToken cancellation)
    {
        if(node.Content is not FolderNode folder)return;
        await LoadTreeListing(node,folder,revision,false,cancellation);
    }
    private static void MergePhysicalChildren(TreeViewNode node,IReadOnlyList<string> paths)
    {
        var wanted=paths.ToHashSet(StringComparer.Ordinal);
        for(int index=node.Children.Count-1;index>=0;index--)
            if(node.Children[index].Content is not FolderNode child||!wanted.Contains(child.Path))node.Children.RemoveAt(index);
        var retained=node.Children.Where(child=>child.Content is FolderNode).ToDictionary(child=>((FolderNode)child.Content).Path,StringComparer.Ordinal);
        for(int index=0;index<paths.Count;index++)
        {
            if(index<node.Children.Count&&node.Children[index].Content is FolderNode current&&current.Path==paths[index])continue;
            retained.TryGetValue(paths[index],out var existing);
            var child=existing??new TreeViewNode{Content=new FolderNode(paths[index],FolderLabel(paths[index])),HasUnrealizedChildren=true};
            if(existing is not null)node.Children.Remove(existing);
            node.Children.Insert(index,child);
        }
        node.HasUnrealizedChildren=false;
    }
    private static string FolderLabel(string path)=>Path.GetFileName(path.TrimEnd('\\')) is {Length:>0} name?name:path;
    private static TreeViewNode CreateFolderNode(string root,string id,string relative)=>new(){Content=new FolderNode(Path.Combine(root,relative),FolderLabel(relative),id,relative,root),HasUnrealizedChildren=true};

    private void QueueTreeRefresh()
    {
        treeRefreshPending=true;
        if(treeRefreshTask is not {IsCompleted:false})treeRefreshTask=RefreshTreeLoop();
    }
    private async Task RefreshTreeLoop()
    {
        while(treeRefreshPending&&!closing)
        {
            treeRefreshPending=false;long revision=rootChangeVersion;var token=physicalTreeStop.Token;
            try{await RefreshTree();}
            catch(OperationCanceledException){}
            catch(Exception ex){if(!closing&&revision==rootChangeVersion&&!token.IsCancellationRequested)ShowError(ex);}
        }
    }
    private async Task RefreshTree()
    {
        if(replacingRoot||catalog is null||activeTreeRoot is not {} treeRoot||treeRoot.Content is not FolderNode {CatalogRoot:not null} folder)return;
        long revision=rootChangeVersion;var token=physicalTreeStop.Token;
        var pending=new Queue<TreeViewNode>();pending.Enqueue(treeRoot);
        while(pending.TryDequeue(out var node))
        {
            if(node.Content is not FolderNode current)continue;
            await LoadTreeListing(node,current,revision,true,token);
            if(closing||revision!=rootChangeVersion||activeTreeRoot!=treeRoot||token.IsCancellationRequested)return;
            foreach(var child in node.Children)if(child.IsExpanded)pending.Enqueue(child);
        }
        await PruneTreeListings();
    }
    private async void ExpandFolder(TreeView sender,TreeViewExpandingEventArgs e)
    {try{await ExpandFolderNode(e.Node);}catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}}
    private async Task ExpandFolderNode(TreeViewNode node)
    {
        using var operation=browserWork.Enter();if(operation is null||closing||replacingRoot||catalog is null||node.Content is not FolderNode folder)return;
        long revision=rootChangeVersion;var token=physicalTreeStop.Token;
        try
        {
            if(folder.PageOffset is not null)return;
            if(folder.CatalogRoot!=rootId||!string.Equals(folder.BasePath,root,StringComparison.Ordinal))
            {await ReadPhysicalChildren(node,revision,token);return;}
            await LoadTreeListing(node,folder,revision,true,token);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!closing&&revision==rootChangeVersion&&!token.IsCancellationRequested)ShowError(ex);}
    }
    private async Task LoadTreeListing(TreeViewNode node,FolderNode folder,long revision,bool indexed,CancellationToken cancellation)
    {
        using var operation=browserWork.Enter();if(operation is null||closing||folder.PageOffset is not null)return;
        long request=treeListingRequests.GetValueOrDefault(node)+1;treeListingRequests[node]=request;
        await treeListingSlots.WaitAsync(cancellation);DirectoryListing? candidate=null;
        try
        {
            if(closing||revision!=rootChangeVersion||cancellation.IsCancellationRequested||treeListingRequests.GetValueOrDefault(node)!=request)return;
            if(verifyTreeListingReadBarrier is {} readBarrier)await readBarrier(folder,indexed,cancellation);
            candidate=indexed?await catalog!.ReadChildDirectories(folder.CatalogRoot!,folder.Relative,folder.BasePath,TreeListingDirectory,cancellation)
                :await FolderChildren.Read(folder.Path,TreeListingDirectory,cancellation);
            if(verifyTreeListingLoadedBarrier is {} loadedBarrier)await loadedBarrier(folder,indexed,cancellation);
            bool Stale()=>closing||revision!=rootChangeVersion||cancellation.IsCancellationRequested||treeListingRequests.GetValueOrDefault(node)!=request;
            if(Stale())return;
            while(!Stale())
            {
                treeListings.TryGetValue(node,out var previous);treePageIntents.TryGetValue(node,out var intent);
                long wanted=intent?.Start??previous?.Start??0,start=0;
                if(previous is not null&&wanted>0)
                {
                    var oldPage=await previous.Listing.ReadPage(Math.Min(wanted,previous.Listing.Count),cancellation);
                    start=Math.Min(wanted,Math.Max(0,(candidate.Count-1)/DirectoryListing.PageSize*DirectoryListing.PageSize));
                    if(oldPage.Count>0&&await candidate.Find(oldPage[0],cancellation) is {} anchor)start=anchor/DirectoryListing.PageSize*DirectoryListing.PageSize;
                }
                var paths=await candidate.ReadPage(start,cancellation);if(Stale())return;
                if(treePageIntents.GetValueOrDefault(node)!=intent)continue;
                var state=new TreeListing(candidate,start,indexed?folder:folder with{CatalogRoot=null});
                ApplyTreePage(node,state,paths);treeListings[node]=state;candidate=null;
                if(intent is not null)treePageIntents[node]=intent with{Start=start};
                if(previous is not null)await previous.Listing.DisposeAsync();
                break;
            }
        }
        finally{if(candidate is not null)await candidate.DisposeAsync();treeListingSlots.Release();}
    }
    private void ApplyTreePage(TreeViewNode node,TreeListing state,IReadOnlyList<string> paths)
    {
        // An ancestor loaded from the filesystem must not keep an old catalog
        // routing tag: expanding it again would replace fresh siblings with stale rows.
        node.Content=state.Folder;
        var selectedBefore=FolderTree.SelectedNode;
        // Keep the selected ancestor chain reachable even while browsing another sibling page.
        TreeViewNode? pinned=activeTreeRoot;
        while(pinned?.Parent is {} parent&&!ReferenceEquals(parent,node))pinned=parent;
        if(!ReferenceEquals(pinned?.Parent,node))pinned=null;
        var page=paths.ToList();if(pinned?.Content is FolderNode active&&!page.Contains(active.Path,StringComparer.Ordinal))page.Add(active.Path);
        for(int index=node.Children.Count-1;index>=0;index--)if(node.Children[index].Content is FolderNode {PageOffset:not null})node.Children.RemoveAt(index);
        MergePhysicalChildren(node,page);
        if(state.Folder.CatalogRoot is {} catalogId)
            foreach(var child in node.Children)
                if(child.Content is FolderNode folder)
                {
                    var updated=folder with{CatalogRoot=catalogId,BasePath=state.Folder.BasePath,Relative=Path.GetRelativePath(state.Folder.BasePath,folder.Path)};
                    if(updated!=folder)child.Content=updated;
                }
        long count=state.Listing.Count,start=state.Start;
        void Button(string label,long target)=>node.Children.Add(new TreeViewNode{Content=new FolderNode(state.Folder.Path,label,PageOffset:target),HasUnrealizedChildren=false});
        if(start>0){Button("⏮ 第一页",0);Button("← 上一页",Math.Max(0,start-DirectoryListing.PageSize));}
        if(start+DirectoryListing.PageSize<count)
        {
            Button($"下一页 →（当前 {start+1:N0}–{Math.Min(start+DirectoryListing.PageSize,count):N0} / {count:N0}）",start+DirectoryListing.PageSize);
            Button("最后一页 ⏭",(count-1)/DirectoryListing.PageSize*DirectoryListing.PageSize);
        }
        // Updating children can clear WinUI's single selection, even if the selected
        // parent itself was retained. Preserve selection across this synchronous edit;
        // do not select a removed node or move the user's viewport during scan refresh.
        if(selectedBefore is not null&&FolderTree.SelectedNode!=selectedBefore)
        {
            var top=selectedBefore;while(!FolderTree.RootNodes.Contains(top)&&top.Parent is {} ancestor)top=ancestor;
            if(FolderTree.RootNodes.Contains(top))FolderTree.SelectedNode=selectedBefore;
        }
    }
    private async Task ChangeTreePage(TreeViewNode node,long start)
    {
        using var operation=browserWork.Enter();if(operation is null||closing||replacingRoot||!treeListings.ContainsKey(node))return;
        long revision=rootChangeVersion,version=++treePageVersion;var token=physicalTreeStop.Token;treePageIntents[node]=new(version,start);
        while(!closing&&!token.IsCancellationRequested&&revision==rootChangeVersion&&treePageIntents.TryGetValue(node,out var intent)&&intent.Version==version&&treeListings.TryGetValue(node,out var previous))
        {
            start=Math.Min(intent.Start,Math.Max(0,(previous.Listing.Count-1)/DirectoryListing.PageSize*DirectoryListing.PageSize));
            IReadOnlyList<string> paths;
            try{paths=await previous.Listing.ReadPage(start,token);}
            catch(ObjectDisposedException) when(treeListings.TryGetValue(node,out var replaced)&&!ReferenceEquals(replaced.Listing,previous.Listing)){continue;}
            if(verifyTreePageReadBarrier is not null)await verifyTreePageReadBarrier(start);
            if(closing||token.IsCancellationRequested||revision!=rootChangeVersion||treePageIntents.GetValueOrDefault(node)?.Version!=version||!treeListings.TryGetValue(node,out var current))return;
            if(!ReferenceEquals(previous.Listing,current.Listing))continue;
            var next=current with{Start=start};ApplyTreePage(node,next,paths);treeListings[node]=next;await PruneTreeListings();return;
        }
    }
    private async Task PruneTreeListings(bool all=false)
    {
        var reachable=new HashSet<TreeViewNode>();var pending=new Queue<TreeViewNode>(FolderTree.RootNodes);
        if(!all)while(pending.TryDequeue(out var node)){reachable.Add(node);foreach(var child in node.Children)pending.Enqueue(child);}
        foreach(var node in treeListings.Keys.Where(node=>!reachable.Contains(node)).ToArray())
        {var state=treeListings[node];treeListings.Remove(node);treeListingRequests.Remove(node);treePageIntents.Remove(node);await state.Listing.DisposeAsync();}
        foreach(var node in treeListingRequests.Keys.Where(node=>!reachable.Contains(node)).ToArray())treeListingRequests.Remove(node);
    }
}
