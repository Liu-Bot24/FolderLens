using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<FolderNode,bool,CancellationToken,Task>? verifyTreeListingReadBarrier;
    private Func<FolderNode,bool,CancellationToken,Task>? verifyTreeListingLoadedBarrier;

    private async Task VerifyTreeNavigationCancellation(string source,Dictionary<string,object> report)
    {
        string target=Path.Combine(source,"B");
        var failures=new List<string>();
        async Task SettleTree()
        {
            if(treeRefreshTask is {} refresh)await refresh.WaitAsync(TimeSpan.FromSeconds(10));
            if(physicalTreeTask is {} physical)await physical.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(()=>treeListingSlots.CurrentCount==2,TimeSpan.FromSeconds(10));
        }
        TreeViewNode AddProbeNode(string label)
        {
            var node=new TreeViewNode{Content=new FolderNode(root,label,rootId,"",root)};
            node.Children.Add(new TreeViewNode{Content=new FolderNode(Path.Combine(root,"untouched"),"untouched")});
            FolderTree.RootNodes.Add(node);return node;
        }
        await OpenRoot(source);await SettleTree();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens=new List<CancellationToken>();
        var nodes=new[]{AddProbeNode("verify-tree-cancel-0"),AddProbeNode("verify-tree-cancel-1")};
        Task[] pending=[];
        try
        {
            // Exercise the actual indexed expansion entry point. Both old reads
            // hold a tree slot before their catalog query starts.
            verifyTreeListingReadBarrier=async(folder,indexed,token)=>
            {
                if(!indexed||!folder.Label.StartsWith("verify-tree-cancel-",StringComparison.Ordinal))return;
                tokens.Add(token);if(tokens.Count==2)entered.TrySetResult();
                await release.Task.WaitAsync(token);
            };
            pending=nodes.Select(ExpandFolderNode).ToArray();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(treeListingSlots.CurrentCount!=0)throw new InvalidOperationException("Old tree reads did not occupy both slots.");
            await OpenRoot(target).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAny(Task.WhenAll(pending),Task.Delay(1000));
            bool cancelled=tokens.Count==2&&tokens.All(token=>token.IsCancellationRequested);
            bool retired=pending.All(task=>task.IsCompleted);
            report["oldIndexedTreeTokensCancelled"]=cancelled;
            report["oldIndexedTreeReadsRetiredWithoutRelease"]=retired;
            report["treeSlotsAfterNavigation"]=treeListingSlots.CurrentCount;
            if(!cancelled||!retired)failures.Add("Directory navigation did not cancel and retire old indexed tree reads.");
        }
        finally
        {
            verifyTreeListingReadBarrier=null;release.TrySetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            foreach(var node in nodes)FolderTree.RootNodes.Remove(node);
            await PruneTreeListings();await SettleTree();
        }
        report["treeSlotsReturnedAfterCleanup"]=treeListingSlots.CurrentCount==2;

        // A provider may finish just as cancellation arrives. Hold the already
        // materialized old listing until the new directory has been published.
        await OpenRoot(source);await SettleTree();
        var lateNode=AddProbeNode("verify-tree-late");
        var originalChild=lateNode.Children.Single();
        var lateEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken lateToken=default;Task? lateRead=null;
        try
        {
            verifyTreeListingLoadedBarrier=async(folder,indexed,token)=>
            {
                if(!indexed||folder.Label!="verify-tree-late")return;
                lateToken=token;lateEntered.TrySetResult();await lateRelease.Task;
            };
            lateRead=ExpandFolderNode(lateNode);
            await lateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await OpenRoot(target).WaitAsync(TimeSpan.FromSeconds(10));
            var acceptedTree=activeTreeRoot;string acceptedRoot=root;long acceptedVersion=rootChangeVersion;
            lateRelease.TrySetResult();await lateRead.WaitAsync(TimeSpan.FromSeconds(5));
            bool unchanged=lateNode.Children.Count==1&&ReferenceEquals(lateNode.Children[0],originalChild)&&
                !treeListings.ContainsKey(lateNode)&&root==acceptedRoot&&rootChangeVersion==acceptedVersion&&
                ReferenceEquals(activeTreeRoot,acceptedTree)&&RootPath.Text==target;
            report["lateTreeTokenCancelled"]=lateToken.IsCancellationRequested;
            report["lateTreeCandidateCannotPublishIntoNewNavigation"]=unchanged;
            if(!lateToken.IsCancellationRequested)failures.Add("Already completed old tree providers keep a live navigation token.");
            if(!unchanged)failures.Add("A late old tree listing overwrote state after directory navigation.");
        }
        finally
        {
            verifyTreeListingLoadedBarrier=null;lateRelease.TrySetResult();
            if(lateRead is not null)await lateRead.WaitAsync(TimeSpan.FromSeconds(10));
            FolderTree.RootNodes.Remove(lateNode);await PruneTreeListings();await SettleTree();
        }
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" ",failures));
        report["status"]="PASS";
    }
}
