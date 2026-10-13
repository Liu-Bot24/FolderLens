using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyBrowsingAnchor(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        source=Path.Combine(source,"browsing-anchor");
        async Task Add(string directory,string prefix,int count)
        {
            string folder=Path.Combine(source,directory);Directory.CreateDirectory(folder);
            for(int i=0;i<count;i++)await File.WriteAllBytesAsync(Path.Combine(folder,$"{prefix}-{i:D3}.png"),sample);
        }
        await Add("A","middle",80);await Add("B","middle",80);
        folderGrouping=new(true,"first","logicalBytes","desc","all");sortDescending=false;SelectTag(SortField,"name");
        ChooseBrowserView(ThumbnailMode,new RoutedEventArgs());
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;await RefreshQuery();Shell.UpdateLayout();
        async Task<FileRow> Row(string path)
        {
            long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,path,lifetime.Token)??throw new InvalidOperationException("Fixture file missing: "+path);
            var row=(FileRow)results![checked((int)ordinal)]!;await EnsureCurrentRowLoaded(row,lifetime.Token,true);return row;
        }
        async Task Browse(FileRow row,bool choose)
        {
            AcceptBrowserInteraction(changesViewport:true);ActiveBrowser.ScrollIntoView(row,ScrollIntoViewAlignment.Leading);
            await WaitUntil(()=>IsThumbnailInViewport(ActiveBrowser,row)&&!browserMovementPending,TimeSpan.FromSeconds(5));
            if(choose){ActiveBrowser.SelectedItem=row;await WaitUntil(()=>ReferenceEquals(selected,row),TimeSpan.FromSeconds(5));}
            Shell.UpdateLayout();
        }
        double Top(FileRow row)=>ActiveBrowser.ContainerFromItem(row) is FrameworkElement element
            ?element.TransformToVisual(ActiveBrowser).TransformPoint(new(0,0)).Y:double.NaN;
        async Task Check(FileRow row,double top,string label,bool chosen=true)
        {
            try{await WaitUntil(()=>IsThumbnailInViewport(ActiveBrowser,row)&&Math.Abs(Top(row)-top)<=1&&(!chosen||ReferenceEquals(ActiveBrowser.SelectedItem,row)&&SelectedOrdinals(ActiveBrowser).Any(range=>row.Ordinal>=range.Start&&row.Ordinal<range.End)),TimeSpan.FromSeconds(5));}
            finally{double actual=Top(row);report[label]=new{expectedTop=top,actualTop=double.IsFinite(actual)?actual:(double?)null,visible=IsThumbnailInViewport(ActiveBrowser,row),selected=ReferenceEquals(ActiveBrowser.SelectedItem,row),expectedOrdinal=row.Ordinal,selectedIndex=ActiveBrowser.SelectedIndex,ranges=SelectedOrdinals(ActiveBrowser).ToArray()};}
        }
        async Task Scan()
        {
            await new DirectoryIndexer(catalog!,ScanWorkerClient.FindExecutable(ScanWorkerDirectory)).Scan(rootId,source,epoch,true,[],null,lifetime.Token);
            await RefreshQuery(preserveViewport:true,scanPreview:true);
        }

        var chosen=await Row(Path.Combine("B","middle-000.png"));await Browse(chosen,true);
        double selectedTop=Top(chosen);
        if(immersive||fullScreen)throw new InvalidOperationException("List selection unexpectedly opened the viewer.");
        await Add("0-new","before",120);await Scan();
        await Check(chosen,selectedTop,"newGroupBeforeSelection");
        if(resultHandle!.Count!=280)throw new InvalidOperationException("Protecting the view stopped publication of discovered files.");

        await File.WriteAllBytesAsync(Path.Combine(source,"B","capacity.bin"),new byte[100000]);await Scan();
        if(browserGroups![0].Info.RelativePath!="B")throw new InvalidOperationException("Capacity fixture did not reorder the selected group.");
        await Check(chosen,selectedTop,"selectedGroupChangesRank");

        var elsewhere=await Row(Path.Combine("A","middle-040.png"));await Browse(elsewhere,false);
        if(IsThumbnailInViewport(ActiveBrowser,chosen))throw new InvalidOperationException("Scroll-away fixture still shows the old selection.");
        var reading=CapturePublicationAnchor(ActiveBrowser)??throw new InvalidOperationException("No current viewport anchor.");
        await Add("A","aaa",120);await Scan();
        await Check(reading.Row,reading.Top,"scrollAwayKeepsCurrentContent",chosen:false);
        if(IsThumbnailInViewport(ActiveBrowser,chosen))throw new InvalidOperationException("Publication pulled the viewport back to an offscreen selection.");

        ChooseBrowserView(DetailsMode,new RoutedEventArgs());await Browse(chosen,true);selectedTop=Top(chosen);
        await Add("B","aaa",12);await Scan();
        await Check(chosen,selectedTop,"detailsPrependKeepsSelection");

        // A queued candidate must use the selection made while it was being built.
        FileRow? latest=null;double latestTop=0;
        verifyCandidateBarrier=async _=>
        {
            verifyCandidateBarrier=null;
            latest=await Row(Path.Combine("B","middle-010.png"));await Browse(latest,true);latestTop=Top(latest);
        };
        try{await RefreshQuery(preserveViewport:true,scanPreview:true);}
        finally{verifyCandidateBarrier=null;}
        if(latest is null||!ReferenceEquals(selected,latest))throw new InvalidOperationException("Old candidate replaced a newer selection.");
        await Check(latest,latestTop,"newSelectionDuringCandidateWins");
        report["newFilesRemainAvailable"]=resultHandle!.Count==412;
        if(!(bool)report["newFilesRemainAvailable"])throw new InvalidOperationException("New files were hidden to preserve the viewport.");

        // Ordinary startup restores a folder at zero, without selecting a file.
        // Its programmatic ChangeView is not a user beginning to browse.
        ClearResultSelection();ActiveBrowser.SelectedItem=null;
        var startup=CaptureView() with{ScrollOffset=0,ScrollAnchorPath=null,SelectedPath=null,Preview=null};
        await RestoreSavedView(startup,recordHistory:false);
        await WaitUntil(()=>!browserMovementPending&&(FindScrollViewer(ActiveBrowser)?.VerticalOffset??0)<1,TimeSpan.FromSeconds(5));
        await Add("00-start","first",1);await File.WriteAllBytesAsync(Path.Combine(source,"00-start","capacity.bin"),new byte[300000]);await Scan();
        var beginning=await Row(Path.Combine("00-start","first-000.png"));
        await WaitUntil(()=>IsThumbnailInViewport(ActiveBrowser,beginning)&&(FindScrollViewer(ActiveBrowser)?.VerticalOffset??0)<1,TimeSpan.FromSeconds(5));
        report["untouchedStartupRestoreStaysAtStart"]=true;
        report["status"]="PASS";
    }
}
