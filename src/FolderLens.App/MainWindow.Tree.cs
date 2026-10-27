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
        // Isolated verification supplies its own names; it must not probe the
        // user's mapped shares while running generated-image regressions.
        if(!Environment.GetCommandLineArgs().Contains("--verify-refresh"))driveLabelsTask=RefreshDriveLabels();
    }
    private Task? driveLabelsTask;
    private async Task RefreshDriveLabels(Func<string,CancellationToken,Task<string>>? read=null)
    {
        if(computerTreeRoot is not {} computer)return;
        string? executable=read is null?ScanWorkerClient.FindExecutable(ScanWorkerDirectory):null;
        if(read is null&&executable is null){RecordWebView("Drive names unavailable: scan helper missing.");return;}
        read??=(path,token)=>NavigationDriveNames.Read(path,executable!,token);
        using var slots=new SemaphoreSlim(2,2);
        await Task.WhenAll(computer.Children.ToArray().Select(async node=>
        {
            if(node.Content is not FolderNode original)return;
            bool entered=false;
            try
            {
                await slots.WaitAsync(lifetime.Token);entered=true;
                string label=await read(original.Path,lifetime.Token);
                if(!closing&&node.Parent==computer&&node.Content is FolderNode current&&current.Path==original.Path)
                    node.Content=current with{Label=label};
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
            catch(Exception error){if(!closing)RecordWebView("Drive name unavailable: "+error.GetType().Name);}
            finally{if(entered)slots.Release();}
        }));
    }
    private Task? treeRefreshTask;
    private bool treeRefreshPending;
    private long nextTreeRefresh;
    private CancellationTokenSource physicalTreeStop=new();
    private Task? physicalTreeTask;
    private readonly SemaphoreSlim treeListingSlots=new(2,2);
    private readonly Dictionary<TreeViewNode,long> treeListingRequests=[];
    // Children are merged in slices so a folder with tens of thousands of
    // subfolders expands completely without holding the UI thread.
    private const int TreeMergeSlice=256;
    private readonly HashSet<TreeViewNode> fillingTreeNodes=[];
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
            // A node's own case mode says nothing about its name in the parent
            // directory. Without proof of an alias, preserve exact path spelling.
            if(candidate.Content is FolderNode folder&&string.Equals(folder.Path,path,StringComparison.Ordinal))
            {
                activeTreeRoot=candidate;
                for(var ancestor=candidate;ancestor is not null;ancestor=ancestor.Parent)ancestor.IsExpanded=true;
                FolderTree.SelectedNode=candidate;nextTreeRefresh=0;physicalTreeTask=RefreshAncestors(candidate,rootChangeVersion,physicalTreeStop.Token);return;
            }
            if(candidate.Content is FolderNode ancestorFolder&&ancestorFolder.Path.Length>closestLength&&
                path.StartsWith(ancestorFolder.Path.TrimEnd('\\')+"\\",StringComparison.Ordinal))
            {closest=candidate;closestLength=ancestorFolder.Path.Length;}
            foreach(var child in candidate.Children)existing.Enqueue(child);
        }
        TreeViewNode? parent=closest;
        var ancestors=new Stack<string>();
        string? stopAt=(closest?.Content as FolderNode)?.Path;
        for(string? current=path;current is not null&&!string.Equals(current,stopAt,StringComparison.Ordinal);current=Path.GetDirectoryName(current))ancestors.Push(current);
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
        PruneTreeRequests();
    }
    private async void ExpandFolder(TreeView sender,TreeViewExpandingEventArgs e)
    {
        // Re-expanding a node that was just filled while collapsed is not a new request.
        if(fillingTreeNodes.Contains(e.Node))return;
        try{await ExpandFolderNode(e.Node);}catch(OperationCanceledException){}catch(Exception ex){ShowError(ex);}
    }
    private async Task ExpandFolderNode(TreeViewNode node)
    {
        using var operation=browserWork.Enter();if(operation is null||closing||replacingRoot||catalog is null||node.Content is not FolderNode folder)return;
        long revision=rootChangeVersion;var token=physicalTreeStop.Token;
        try
        {
            if(folder.CatalogRoot!=rootId||!string.Equals(folder.BasePath,root,StringComparison.Ordinal))
            {await ReadPhysicalChildren(node,revision,token);return;}
            await LoadTreeListing(node,folder,revision,true,token);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!closing&&revision==rootChangeVersion&&!token.IsCancellationRequested)ShowError(ex);}
    }
    private async Task LoadTreeListing(TreeViewNode node,FolderNode folder,long revision,bool indexed,CancellationToken cancellation)
    {
        using var operation=browserWork.Enter();if(operation is null||closing)return;
        long request=treeListingRequests.GetValueOrDefault(node)+1;treeListingRequests[node]=request;
        bool Stale()=>closing||revision!=rootChangeVersion||cancellation.IsCancellationRequested||treeListingRequests.GetValueOrDefault(node)!=request;
        await treeListingSlots.WaitAsync(cancellation);DirectoryListing? candidate=null;List<string> paths;
        try
        {
            if(Stale())return;
            if(verifyTreeListingReadBarrier is {} readBarrier)await readBarrier(folder,indexed,cancellation);
            candidate=indexed?await catalog!.ReadChildDirectories(folder.CatalogRoot!,folder.Relative,folder.BasePath,TreeListingDirectory,cancellation)
                :await FolderChildren.Read(folder.Path,TreeListingDirectory,cancellation);
            if(verifyTreeListingLoadedBarrier is {} loadedBarrier)await loadedBarrier(folder,indexed,cancellation);
            if(Stale())return;
            // The listing is disk-backed: read it in one pass off the UI thread and
            // keep only the names. Every child is shown; there is no tree paging.
            paths=[..await candidate.ReadAll(cancellation)];
            if(Stale())return;
        }
        finally{if(candidate is not null)await candidate.DisposeAsync();treeListingSlots.Release();}
        await ApplyTreeChildren(node,indexed?folder:folder with{CatalogRoot=null},paths,Stale);
    }
    private async Task<bool> ApplyTreeChildren(TreeViewNode node,FolderNode source,List<string> paths,Func<bool> stale)
    {
        // An ancestor loaded from the filesystem must not keep an old catalog
        // routing tag: expanding it again would replace fresh siblings with stale rows.
        // A drive name can finish loading while this directory is being read.
        // Keep its current presentation, but use this listing's routing tags.
        node.Content=node.Content is FolderNode current&&current.Path==source.Path
            ?source with{Label=current.Label}:source;
        var selectedBefore=FolderTree.SelectedNode;
        // Keep the selected ancestor chain reachable even if the listing missed it.
        TreeViewNode? pinned=activeTreeRoot;
        while(pinned?.Parent is {} parent&&!ReferenceEquals(parent,node))pinned=parent;
        if(!ReferenceEquals(pinned?.Parent,node))pinned=null;
        var wanted=paths.ToHashSet(StringComparer.Ordinal);
        if(pinned?.Content is FolderNode active&&wanted.Add(active.Path))paths.Add(active.Path);
        for(int index=node.Children.Count-1;index>=0;index--)
            if(node.Children[index].Content is not FolderNode child||!wanted.Contains(child.Path))node.Children.RemoveAt(index);
        var retained=node.Children.Where(child=>child.Content is FolderNode).ToDictionary(child=>((FolderNode)child.Content).Path,StringComparer.Ordinal);
        // Each child added under an expanded WinUI tree node costs time proportional
        // to the visible tree, so a first expansion of a wide folder fills the node
        // while it is collapsed and then expands it once.
        bool bulk=node.IsExpanded&&retained.Count==0&&paths.Count>TreeMergeSlice;
        if(bulk){fillingTreeNodes.Add(node);node.IsExpanded=false;}
        try
        {
            for(int index=0;index<paths.Count;index++)
            {
                if(!bulk&&index>0&&index%TreeMergeSlice==0)
                {
                    RestoreTreeSelection(selectedBefore);
                    if(verifyTreeMergeSlice is {} slice)await slice(node,index);
                    await YieldToInput();
                    // A newer listing for this node, or navigation, owns the merge now.
                    if(stale())return false;
                }
                TreeViewNode child;
                if(index<node.Children.Count&&node.Children[index].Content is FolderNode existingAt&&existingAt.Path==paths[index])child=node.Children[index];
                else
                {
                    retained.TryGetValue(paths[index],out var existing);
                    child=existing??new TreeViewNode{Content=new FolderNode(paths[index],FolderLabel(paths[index])),HasUnrealizedChildren=true};
                    if(existing is not null)node.Children.Remove(existing);
                    node.Children.Insert(index,child);
                }
                if(source.CatalogRoot is {} catalogId&&child.Content is FolderNode folder)
                {
                    var updated=folder with{CatalogRoot=catalogId,BasePath=source.BasePath,Relative=Path.GetRelativePath(source.BasePath,folder.Path)};
                    if(updated!=folder)child.Content=updated;
                }
            }
        }
        finally
        {
            if(bulk){node.IsExpanded=true;fillingTreeNodes.Remove(node);}
        }
        node.HasUnrealizedChildren=false;
        RestoreTreeSelection(selectedBefore);
        return true;
    }
    // Updating children can clear WinUI's single selection, even if the selected
    // node itself was retained. Restore it, but never select a removed node or
    // move the user's viewport during a scan refresh.
    private void RestoreTreeSelection(TreeViewNode? selectedBefore)
    {
        if(selectedBefore is null||FolderTree.SelectedNode==selectedBefore)return;
        var top=selectedBefore;while(!FolderTree.RootNodes.Contains(top)&&top.Parent is {} ancestor)top=ancestor;
        if(FolderTree.RootNodes.Contains(top))FolderTree.SelectedNode=selectedBefore;
    }
    // Let input and rendering run between slices of a large tree edit.
    private Task YieldToInput()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>done.TrySetResult()))done.TrySetResult();
        return done.Task;
    }
    private void PruneTreeRequests(bool all=false)
    {
        var reachable=new HashSet<TreeViewNode>();var pending=new Queue<TreeViewNode>(FolderTree.RootNodes);
        if(!all)while(pending.TryDequeue(out var node)){reachable.Add(node);foreach(var child in node.Children)pending.Enqueue(child);}
        foreach(var node in treeListingRequests.Keys.Where(node=>!reachable.Contains(node)).ToArray())treeListingRequests.Remove(node);
    }
}
