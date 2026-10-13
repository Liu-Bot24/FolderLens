using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyUntouchedStartupViewport(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=12;i<256;i++)await File.WriteAllBytesAsync(Path.Combine(source,"A",$"image-{i:D3}.png"),sample);
        folderGrouping=new(true,"first","logicalBytes","desc","all");sortDescending=true;SelectTag(SortField,"name");
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;
        var saved=CaptureView() with{SelectedPath=null,ScrollAnchorPath=null,ScrollOffset=0,Preview=null};
        await OpenRoot(Path.Combine(source,"B"));if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;
        // RestoreDesktop invokes this same programmatic view switch before
        // reopening last-session. That state must not leak into the new root.
        ToggleView(this,new RoutedEventArgs());
        bool firstPageObserved=false,firstPageProtected=false;
        verifyFirstPageBarrier=_=>{firstPageObserved=true;firstPageProtected|=browserContentProtected;return Task.CompletedTask;};
        try{await RestoreSavedView(saved,recordHistory:false);}
        finally{verifyFirstPageBarrier=null;monitor?.Dispose();monitor=null;}
        report["startupAdmission"]=new{firstPageObserved,firstPageProtected};
        if(!firstPageObserved||firstPageProtected)throw new InvalidOperationException("Programmatic desktop restoration marked an untouched first page as actively browsed.");
        var view=ActiveBrowser;var scroll=FindScrollViewer(view)??throw new InvalidOperationException("Missing browser scroller.");
        long interaction=browserInteractionVersion;
        report["beforeLateLayout"]=new{browserMovementPending,browserContentProtected,keepBrowserStart,offset=scroll.VerticalOffset};
        // Model a delayed native anchoring/layout pass, without any input event.
        // The existing late-layout regression uses the same native ChangeView path.
        double displaced=Math.Min(800,scroll.ScrollableHeight/2);
        if(displaced<100)throw new InvalidOperationException("Fixture is too short to expose an unwanted middle position.");
        scroll.ChangeView(null,displaced,null,true);
        try{await WaitUntil(()=>!browserMovementPending&&scroll.VerticalOffset<=1,TimeSpan.FromSeconds(4));}
        finally{report["afterLateLayout"]=new{interactionUnchanged=interaction==browserInteractionVersion,selected=selected is not null,browserMovementPending,browserContentProtected,keepBrowserStart,offset=scroll.VerticalOffset,displaced};}
        if(interaction!=browserInteractionVersion||selected is not null)throw new InvalidOperationException("Untouched fixture acquired user input or selection.");
        report["beforeAppendScan"]=new{rootMatches=string.Equals(root,source,StringComparison.Ordinal),epoch,filterEpoch=submittedBrowserFilter?.ObservedRootEpoch,displayed=resultHandle?.Count};
        await File.WriteAllBytesAsync(Path.Combine(source,"A","zz-first.png"),sample);
        var appended=await new DirectoryIndexer(catalog!,ScanWorkerClient.FindExecutable(ScanWorkerDirectory)).Scan(rootId,source,epoch,true,[],null,lifetime.Token);
        await RefreshQuery(preserveViewport:true,scanPreview:true);
        report["appendScan"]=new{appended.Files,appended.State,appended.Errors,epoch,filterEpoch=submittedBrowserFilter?.ObservedRootEpoch,displayed=resultHandle?.Count,queryBusy};
        await WaitUntil(()=>scroll.VerticalOffset<=1,TimeSpan.FromSeconds(3));
        // Use the same owned/rebindable row read as the browser. A queued scan
        // publication can retire a raw session ID while a direct ReadPage awaits.
        var first=(FileRow)results![0]!;await EnsureCurrentRowLoaded(first,lifetime.Token,true);
        if(Path.GetFileName(first.RelativePath)!="zz-first.png")throw new InvalidOperationException("Background sort was disabled to keep the initial viewport at the top.");
        report["untouchedSortStillPrependsAtZero"]=true;

        // In the same view, actual browsing changes the rule: keep that content.
        var chosen=(FileRow)results![90]!;await EnsureCurrentRowLoaded(chosen,lifetime.Token,true);
        AcceptBrowserInteraction(changesViewport:true);view.ScrollIntoView(chosen,ScrollIntoViewAlignment.Leading);
        await WaitUntil(()=>IsThumbnailInViewport(view,chosen)&&!browserMovementPending,TimeSpan.FromSeconds(4));
        view.SelectedItem=chosen;await WaitUntil(()=>ReferenceEquals(selected,chosen),TimeSpan.FromSeconds(3));
        double top=((FrameworkElement)view.ContainerFromItem(chosen)).TransformToVisual(view).TransformPoint(new(0,0)).Y;
        long ordinal=chosen.Ordinal;
        for(int i=0;i<24;i++)await File.WriteAllBytesAsync(Path.Combine(source,"A",$"zzz-new-{i:D2}.png"),sample);
        await new DirectoryIndexer(catalog!,ScanWorkerClient.FindExecutable(ScanWorkerDirectory)).Scan(rootId,source,epoch,true,[],null,lifetime.Token);
        await RefreshQuery(preserveViewport:true,scanPreview:true);
        await WaitUntil(()=>IsThumbnailInViewport(view,chosen)&&view.ContainerFromItem(chosen) is FrameworkElement element
            &&Math.Abs(element.TransformToVisual(view).TransformPoint(new(0,0)).Y-top)<=1,TimeSpan.FromSeconds(4));
        if(chosen.Ordinal!=ordinal+24||!ReferenceEquals(view.SelectedItem,chosen))throw new InvalidOperationException("Browsing protection disabled sorting or lost selection.");
        report["activeBrowsingKeepsContentWhileSortedItemsPrepend"]=true;
        report["status"]="PASS";
    }
}
