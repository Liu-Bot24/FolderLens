using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    // Exercises the WinUI selection provider without SendInput, activation or
    // desktop pointer movement. This is not a physical Ctrl/Shift-click test.
    private async Task VerifyFunctionalNativeInput(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=0;i<12;i++)await File.WriteAllBytesAsync(Path.Combine(source,"B",$"other-{i:D2}.png"),sample);
        await OpenRoot(source);await SettleFunctionalCollection();
        var checks=new List<object>();report["checks"]=checks;
        var providers=new List<object>();report["providers"]=providers;
        void CheckDesktop()
        {
            if(WindowFocus.IsForeground(this)||AppWindow.IsShownInSwitchers||AppWindow.Position.X>-15000)
                throw new InvalidOperationException("Native-input verification must remain offscreen and inactive.");
        }
        async Task<ISelectionItemProvider> Provider(int index)
        {
            var view=ActiveBrowser;var sourceResults=results??throw new InvalidOperationException("No current result source.");
            var row=(FileRow)sourceResults[index]!;await EnsureCurrentRowLoaded(row,lifetime.Token,true);
            if(!ReferenceEquals(view,ActiveBrowser)||!ReferenceEquals(sourceResults,results))throw new OperationCanceledException("The probe's list changed while loading its item.");
            view.ScrollIntoView(row);Shell.UpdateLayout();
            await WaitUntil(()=>view.ContainerFromItem(row) is FrameworkElement,TimeSpan.FromSeconds(5));
            var element=(FrameworkElement)view.ContainerFromItem(row);
            var containerPeer=FrameworkElementAutomationPeer.FromElement(element)??FrameworkElementAutomationPeer.CreatePeerForElement(element);
            var parent=FrameworkElementAutomationPeer.FromElement(view)??FrameworkElementAutomationPeer.CreatePeerForElement(view);
            // A realized container peer is not a data-item peer. WinUI exposes
            // ISelectionItemProvider on SelectorItemAutomationPeer descendants,
            // not on GridViewItemAutomationPeer/ListViewItemAutomationPeer.
            SelectorItemAutomationPeer itemPeer=parent switch
            {
                GridViewAutomationPeer grid=>new GridViewItemDataAutomationPeer(row,grid),
                ListViewAutomationPeer list=>new ListViewItemDataAutomationPeer(row,list),
                _=>throw new InvalidOperationException("Unsupported native list automation peer: "+parent?.GetType().Name)
            };
            providers.Add(new{index,container=containerPeer?.GetType().Name,containerHasSelectionPattern=containerPeer?.GetPattern(PatternInterface.SelectionItem) is not null,itemPeer=itemPeer.GetType().Name});
            CheckDesktop();return itemPeer;
        }
        foreach(bool details in new[]{false,true})foreach(bool grouped in new[]{false,true})
        {
            CheckDesktop();folderGrouping=new(grouped,"first","name","asc","all");sortDescending=false;
            suppressFilters=true;try{SelectTag(SortField,"name");}finally{suppressFilters=false;}
            ChooseBrowserView(details?DetailsMode:ThumbnailMode,new RoutedEventArgs());await RefreshQuery();
            (await Provider(1)).Select();(await Provider(4)).AddToSelection();(await Provider(7)).AddToSelection();CheckDesktop();
            var added=await FunctionalSelectedItems();if(added.Length!=3)throw new InvalidOperationException("Native additive selection did not retain all three items.");
            (await Provider(4)).RemoveFromSelection();var remaining=await FunctionalSelectedItems();
            if(remaining.Length!=2||remaining.Any(item=>item.Ordinal==4))throw new InvalidOperationException("Native deselection removed the wrong identities.");
            string[] expected=remaining.Select(item=>item.EntryId).Order().ToArray();string prior=resultHandle!.Id;
            if(details)InvokeNativeButton(detailSortButtons[0]);else InvokeNativeButton(Descending);
            await WaitUntil(()=>!queryBusy&&resultHandle?.Id!=prior,TimeSpan.FromSeconds(5));
            var actual=(await FunctionalSelectedItems()).Select(item=>item.EntryId).Order().ToArray();bool same=expected.SequenceEqual(actual);CheckDesktop();
            checks.Add(new{details,grouped,nativeAdd=added.Length,nativeRemove=remaining.Length,sameIdentityAfterSort=same,offscreen=true});
            if(!same)throw new InvalidOperationException("Sorting changed native provider selection identities.");
        }
        report["mechanism"]="Native WinUI ISelectionItemProvider and native sort button, no physical keyboard or pointer injection";
        report["desktopNotAcquired"]=true;report["status"]="PASS";
    }
}
