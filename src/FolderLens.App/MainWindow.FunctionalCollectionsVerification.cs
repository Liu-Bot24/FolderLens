using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<ContentDialog,Task>? verifyCollectionDialogDriver;
    private Action? verifyCollectionManagerClosed;
    private Func<CancellationToken,Task>? verifyCollectionLocationBarrier;
    private static void InvokeNativeButton(Button button)=>((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<T> VerificationDescendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;
            foreach(var descendant in VerificationDescendants<T>(child))yield return descendant;
        }
    }
    private async Task RunCollectionManager(Func<ContentDialog,Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Exception? failure=null;
        verifyCollectionManagerClosed=()=>done.TrySetResult();
        verifyCollectionDialogDriver=async dialog=>{try{await action(dialog);}catch(Exception error){failure=error;throw;}};
        try{ManageCollections(this,new());await done.Task.WaitAsync(TimeSpan.FromSeconds(20));if(failure is not null)throw failure;}
        finally{verifyCollectionDialogDriver=null;verifyCollectionManagerClosed=null;}
    }
    private Task ManagerSelect(string id,Func<ContentDialog,StackPanel,ListView,Task> action)
        =>RunCollectionManager(async dialog=>
        {
            var panel=(StackPanel)dialog.Content;var list=(ListView)panel.Children[0];
            list.SelectedItem=list.Items.Cast<FileCollection>().Single(item=>item.Id==id);dialog.UpdateLayout();await action(dialog,panel,list);
        });
    private Task ManagerOpen(string id)=>ManagerSelect(id,(dialog,_,_)=>
    {InvokeNativeButton(VerificationDescendants<Button>(dialog).Single(button=>button.Content as string=="打开所选"));return Task.CompletedTask;});
    private Task ManagerDelete(string id)=>ManagerSelect(id,async(dialog,panel,_)=>
    {
        var button=((StackPanel)panel.Children[2]).Children.OfType<Button>().Single(item=>item.Content as string=="删除收藏夹");
        InvokeNativeButton(button);if(button.Content as string!="确认删除")throw new InvalidOperationException("Delete did not enter confirmation state.");
        InvokeNativeButton(button);await WaitUntil(()=>!fileCollections.Any(item=>item.Id==id)&&((TextBlock)panel.Children[3]).Text.Contains("已删除"),TimeSpan.FromSeconds(8));dialog.Hide();
    });
    private async Task SettleFunctionalCollection()
    {
        if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;if(queryBusy)await queryCompletion;
        if(collectionScanRefreshTask is not null)await collectionScanRefreshTask;monitor?.Dispose();monitor=null;
    }
    private async Task VerifyFunctionalHistory(string source,Dictionary<string,object> report,bool forward)
    {
        await OpenRoot(source);await SettleFunctionalCollection();
        var a=await catalog!.CreateCollection("Audit A");var b=await catalog.CreateCollection("Audit B");await RefreshCollectionsTree();
        await ManagerOpen(a.Id);await SettleFunctionalCollection();
        if(forward)
        {
            await ManagerOpen(b.Id);await SettleFunctionalCollection();InvokeNativeButton(BackButton);
            await WaitUntil(()=>activeCollectionId==a.Id&&!restoringView&&!queryBusy,TimeSpan.FromSeconds(5));
        }
        await ManagerDelete(a.Id);report["emptyAfterDelete"]=root.Length==0&&activeCollectionId is null&&resultHandle is null;
        bool enabled=forward?ForwardButton.IsEnabled:BackButton.IsEnabled;report["historyButtonEnabled"]=enabled;
        long revision=navigationHistory.Revision;InvokeNativeButton(forward?ForwardButton:BackButton);
        bool reached=false;try{await WaitUntil(()=>forward?activeCollectionId==b.Id&&!restoringView:root==source&&!restoringView,TimeSpan.FromSeconds(4));reached=true;}catch(TimeoutException){}
        report["acceptedTarget"]=reached;report["historyConsumed"]=navigationHistory.Revision>revision;
        if(!enabled||!reached)throw new InvalidOperationException("Deleting the current collection left a valid history target unreachable.");
        if(forward?navigationHistory.CanGoBack&&navigationHistory.PeekBack().Root.Length==0:navigationHistory.CanGoForward)throw new InvalidOperationException("Empty/deleted source entered reverse history.");
        report["status"]="PASS";
    }
    private async Task VerifyFunctionalCollectionNames(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=0;i<100;i++)await File.WriteAllBytesAsync(Path.Combine(source,"A",$"extra-{i:D3}.png"),sample);
        await OpenRoot(source);await SettleFunctionalCollection();
        var a=await catalog!.CreateCollection("Old label");var other=await catalog.CreateCollection("Duplicate label");await RefreshCollectionsTree();
        var members=(await catalog.ReadPage(resultHandle!.Id,0,64)).Select(item=>item.EntryId).ToArray();
        var retainedNode=collectionsTreeRoot!.Children.Single(node=>node.Content is CollectionNode c&&c.Id==a.Id);SavedView? savedCollection=null;
        var checks=new List<object>();var failures=new List<string>();report["checks"]=checks;
        foreach(string scope in new[]{"active","include","exclude"})
        {
            async Task Progress(string step)=>await File.WriteAllTextAsync(Path.Combine(dataDirectory,"collection-names-progress.json"),System.Text.Json.JsonSerializer.Serialize(new{scope,step,root,activeCollectionId,checks}));
            await Progress("navigate");
            if(scope=="active"){await ManagerOpen(a.Id);savedCollection=CaptureAcceptedView();}
            else
            {
                if(scope=="include")await catalog.ChangeCollectionMembers([a.Id],members,true);
                await OpenRoot(source);await SettleFunctionalCollection();includedCollectionIds=scope=="include"?[a.Id]:[];excludedCollectionIds=scope=="exclude"?[a.Id]:[];await ApplyBrowserFilters();
                ActiveBrowser.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(2,3));ActiveBrowser.ScrollIntoView(results![20],ScrollIntoViewAlignment.Leading);
                await WaitUntil(()=>!previewLoading&&!browserMovementPending,TimeSpan.FromSeconds(5));
            }
            await SettleFunctionalCollection();string? snapshot=resultHandle?.Id;var selectionBefore=SelectedOrdinals(ActiveBrowser).ToArray();
            double offset=FindScrollViewer(ActiveBrowser)?.VerticalOffset??0;string label=(scope=="include"?"ZZ ":"AA ")+scope;
            includedCollectionIds=[other.Id]; // Keep an unapplied draft distinct from the submitted filter.
            await Progress("rename");
            await ManagerSelect(a.Id,async(dialog,panel,list)=>
            {
                ((TextBox)panel.Children[1]).Text=label;
                InvokeNativeButton(((StackPanel)panel.Children[2]).Children.OfType<Button>().Single(button=>button.Content as string=="重命名"));
                await WaitUntil(()=>list.SelectedItem is FileCollection item&&item.Name==label,TimeSpan.FromSeconds(5));dialog.Hide();
            });
            bool names=ActiveFilterSummary.Text.Contains(label)&&(scope!="active"||RootPath.Text=="收藏夹："+label);
            bool stable=snapshot==resultHandle?.Id&&selectionBefore.SequenceEqual(SelectedOrdinals(ActiveBrowser))&&Math.Abs(offset-(FindScrollViewer(ActiveBrowser)?.VerticalOffset??0))<1;
            bool draft=!ActiveFilterSummary.Text.Contains(other.Name);
            var currentNode=collectionsTreeRoot!.Children.Single(node=>node.Content is CollectionNode c&&c.Id==a.Id);
            bool nodeStable=ReferenceEquals(retainedNode,currentNode);int nodeIndex=collectionsTreeRoot.Children.IndexOf(currentNode);
            bool ordered=nodeIndex==(scope=="include"?1:0);
            checks.Add(new{scope,names,stable,draft,nodeStable,nodeIndex,ordered,address=RootPath.Text,summary=ActiveFilterSummary.Text});if(!names||!stable||!draft||!nodeStable||!ordered)failures.Add(scope);
            await Progress("rename-completed");
            string address=RootPath.Text,summary=ActiveFilterSummary.Text;
            await ManagerSelect(a.Id,async(dialog,panel,_)=>
            {
                ((TextBox)panel.Children[1]).Text=other.Name;
                InvokeNativeButton(((StackPanel)panel.Children[2]).Children.OfType<Button>().Single(button=>button.Content as string=="重命名"));
                await WaitUntil(()=>((TextBlock)panel.Children[3]).Text.Length>0,TimeSpan.FromSeconds(5));dialog.Hide();
            });
            if(RootPath.Text!=address||ActiveFilterSummary.Text!=summary||CollectionLabel(a.Id)!=label)throw new InvalidOperationException("Rejected rename changed a published label.");
            await Progress("rejected-rename-completed");
        }
        await RestoreSavedView(savedCollection!);await SettleFunctionalCollection();
        bool restored=RootPath.Text=="收藏夹："+CollectionLabel(a.Id)&&ActiveFilterSummary.Text.Contains(CollectionLabel(a.Id));
        await NavigateHistory(false);await NavigateHistory(true);await SettleFunctionalCollection();
        bool history=activeCollectionId==a.Id&&RootPath.Text=="收藏夹："+CollectionLabel(a.Id);
        report["savedAndHistoricalViewsUseRenamedId"]=restored&&history;if(!restored||!history)failures.Add("saved/history names");
        report["failures"]=failures;if(failures.Count>0)throw new InvalidOperationException("Rename left stale derived names: "+string.Join(",",failures));report["status"]="PASS";
    }
    private async Task VerifyFunctionalCollectionNavigation(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);await SettleFunctionalCollection();var a=await catalog!.CreateCollection("Empty A");var b=await catalog.CreateCollection("Empty B");await RefreshCollectionsTree();
        var checks=new List<object>();var failures=new List<string>();report["checks"]=checks;
        async Task Check(string label,string id)
        {
            await SettleFunctionalCollection();bool same=activeCollectionId==id&&RootPath.Text=="收藏夹："+CollectionLabel(id)&&FolderTree.SelectedNode?.Content is CollectionNode node&&node.Id==id;
            checks.Add(new{label,same,active=activeCollectionId,tree=(FolderTree.SelectedNode?.Content as CollectionNode)?.Id,address=RootPath.Text});if(!same)failures.Add(label);
        }
        await ManagerOpen(a.Id);await Check("managerA",a.Id);var saved=CaptureAcceptedView()!;
        await ManagerOpen(b.Id);await Check("managerB",b.Id);
        InvokeNativeButton(BackButton);await WaitUntil(()=>activeCollectionId==a.Id&&!restoringView,TimeSpan.FromSeconds(5));await Check("backA",a.Id);
        InvokeNativeButton(ForwardButton);await WaitUntil(()=>activeCollectionId==b.Id&&!restoringView,TimeSpan.FromSeconds(5));await Check("forwardB",b.Id);
        await RestoreSavedView(saved);await Check("savedA",a.Id);
        await OpenRoot(source);await SettleFunctionalCollection();InvokeNativeButton(BackButton);await WaitUntil(()=>activeCollectionId==a.Id&&!restoringView,TimeSpan.FromSeconds(5));await Check("directoryBackA",a.Id);
        report["failures"]=failures;if(failures.Count>0)throw new InvalidOperationException("Accepted collection navigation did not update tree identity: "+string.Join(",",failures));report["status"]="PASS";
    }
    private async Task VerifyFunctionalHistoryBoundaries(string source,Dictionary<string,object> report)
    {
        var a=await catalog!.CreateCollection("Boundary A");var b=await catalog.CreateCollection("Boundary B");await RefreshCollectionsTree();
        await ManagerOpen(a.Id);await ManagerDelete(a.Id);
        if(BackButton.IsEnabled||ForwardButton.IsEnabled)throw new InvalidOperationException("Deleting the only page left enabled history buttons.");
        report["noHistoryDisabled"]=true;
        await ManagerOpen(b.Id);await OpenRoot(source);await SettleFunctionalCollection();var handle=resultHandle;
        await ManagerDelete(b.Id);
        if(root!=source||!ReferenceEquals(handle,resultHandle)||BackButton.IsEnabled)throw new InvalidOperationException("Deleting a noncurrent collection changed the view or retained its dead history target.");
        report["noncurrentDeletePrunesOnlyDeadHistory"]=true;
        string missing=Path.Combine(source,"former-directory");Directory.CreateDirectory(missing);await OpenRoot(missing);await SettleFunctionalCollection();await OpenRoot(source);await SettleFunctionalCollection();
        long revision=navigationHistory.Revision;string target=navigationHistory.PeekBack().Root;
        verifyRootAdmissionBarrier=_=>throw new IOException("FunctionalHistoryAdmissionFailure");
        try{await NavigateHistory(false);}catch(IOException){}finally{verifyRootAdmissionBarrier=null;}
        if(root!=source||navigationHistory.Revision!=revision||navigationHistory.PeekBack().Root!=target)throw new InvalidOperationException("Rejected history navigation consumed its target.");
        report["rejectedTargetUnconsumed"]=true;
        var c=await catalog.CreateCollection("Boundary C");var d=await catalog.CreateCollection("Boundary D");await RefreshCollectionsTree();await ManagerOpen(c.Id);await ManagerOpen(d.Id);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyCollectionRefreshBarrier=async token=>{if(activeCollectionId==c.Id){entered.TrySetResult();await release.Task.WaitAsync(token);}};
        try
        {
            InvokeNativeButton(BackButton);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));InvokeNativeButton(ForwardButton);
            await WaitUntil(()=>activeCollectionId==d.Id&&!restoringView&&!queryBusy,TimeSpan.FromSeconds(5));release.TrySetResult();await Task.Delay(100);
            if(activeCollectionId!=d.Id||FolderTree.SelectedNode?.Content is not CollectionNode node||node.Id!=d.Id)throw new InvalidOperationException("A cancelled history load overwrote rapid forward navigation.");
        }
        finally{release.TrySetResult();verifyCollectionRefreshBarrier=null;}
        report["rapidForwardOwnsPage"]=true;report["status"]="PASS";
    }
    private async Task VerifyFunctionalCollectionLocationBoundaries(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);await SettleFunctionalCollection();var a=await catalog!.CreateCollection("Missing node A");var b=await catalog.CreateCollection("Missing node B");await RefreshCollectionsTree();
        SavedView View(string id)=>new("collection:"+id,new(){RootId="collection:"+id,CollectionId=id,Kinds=[],ShowHidden=true},null,0,false);
        void Evict(string id){var node=collectionsTreeRoot!.Children.Single(node=>node.Content is CollectionNode c&&c.Id==id);collectionsTreeRoot.Children.Remove(node);collectionsTreeRoot.IsExpanded=false;}
        Evict(a.Id);await RestoreSavedView(View(a.Id));await WaitUntil(()=>collectionLocationStop is null,TimeSpan.FromSeconds(5));
        bool visible=collectionsTreeRoot!.IsExpanded&&FolderTree.SelectedNode?.Content is CollectionNode c&&c.Id==a.Id;report["missingNodeSelectedAndVisible"]=visible;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var retired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyCollectionLocationBarrier=async _=>{entered.TrySetResult();await release.Task;retired.TrySetResult();throw new IOException("RetiredCollectionLocationFixture");};
        Evict(b.Id);await RestoreSavedView(View(b.Id));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await DeleteCollectionAndRefresh(b.Id);const string expected="Current empty page owns status";Status.Text=expected;
            release.TrySetResult();await retired.Task;await Task.Delay(80);
            bool owned=Status.Text==expected&&activeCollectionId is null&&root.Length==0;report["retiredErrorCannotOverwriteEmptyPage"]=owned;
            if(!visible||!owned)throw new InvalidOperationException("Missing collection compensation did not preserve visible selection/error ownership.");
        }
        finally{release.TrySetResult();verifyCollectionLocationBarrier=null;}
        report["status"]="PASS";
    }
}
