using System.Globalization;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyCollectionDirectoryContext(string source,Dictionary<string,object> report)
    {
        string first=Path.Combine(source,"A","image-00.png"),child=Path.Combine(source,"A","child");Directory.CreateDirectory(child);
        File.Copy(first,Path.Combine(child,"child.png"));File.Copy(first,Path.Combine(source,"B","other.png"));
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var collection=await catalog!.CreateCollection("source rules",lifetime.Token);
        var items=(await catalog.ReadFirstPage(CurrentFilter(),lifetime.Token)).Items;
        await catalog.ChangeCollectionItems([collection.Id],items,true,lifetime.Token);await RefreshCollectionsTree();await OpenCollection(collection.Id);
        var named=CurrentFilter() with{DirectoryRules=[new("include","name","equals","A")]};
        ApplySavedFilter(named);await ApplyBrowserFilters();
        if(resultHandle?.Count!=13)throw new InvalidOperationException("Refreshed collection name rules lost their source directory.");
        string path=await catalog.CollectionSourceDirectory(collection.Id,Path.Combine(source,"A"),lifetime.Token);
        var absolute=CurrentFilter() with{DirectoryRules=[new("include","sourcePath","equals",path)]};
        ApplySavedFilter(absolute);await ApplyBrowserFilters();
        var preview=await catalog.PreviewDirectoryRules(rootId,absolute.DirectoryRules,lifetime.Token,collection.Id);
        if(resultHandle?.Count!=13||preview.VisibleFiles!=13||preview.HiddenFiles!=1)throw new InvalidOperationException("Collection picker, preview and result query disagree.");
        var original=CaptureView();var legacy=original with{Filter=original.Filter with{SchemaVersion=1,DirectoryRules=[new("include","path","equals","A")]}};
        string originalJson=System.Text.Json.JsonSerializer.Serialize(legacy);
        async Task Migrate(bool apply)
        {
            ContentDialog? dialog=null;TextBox? input=null;verifyCollectionRuleMigration=(opened,field)=>{dialog=opened;input=field;};
            Task restoring=RestoreSavedView(legacy,false);
            try
            {
                await WaitUntil(()=>dialog?.IsLoaded==true,TimeSpan.FromSeconds(5));
                input!.Text=apply?source:"not-an-absolute-root";dialog!.UpdateLayout();
                void Invoke(string name)=>((IInvokeProvider)new ButtonAutomationPeer(InputDescendants(dialog).OfType<Button>().Single(button=>button.Name==name)).GetPattern(PatternInterface.Invoke)).Invoke();
                Invoke("PrimaryButton");
                if(!apply)
                {
                    await Task.Yield();
                    if(restoring.IsCompleted||resultHandle?.Count!=13)throw new InvalidOperationException("Invalid migration origin was applied.");
                    Invoke("CloseButton");
                }
                await restoring.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally{verifyCollectionRuleMigration=null;if(dialog?.IsLoaded==true)dialog.Hide();await restoring;}
        }
        await Migrate(false);
        if(CurrentFilter().DirectoryRules[0].Target!="sourcePath"||originalJson!=System.Text.Json.JsonSerializer.Serialize(legacy))
            throw new InvalidOperationException("Cancelled migration changed accepted or original rules.");
        await Migrate(true);
        if(CurrentFilter().SchemaVersion!=2||CurrentFilter().DirectoryRules[0].BasePath!=source||resultHandle?.Count!=13)
            throw new InvalidOperationException("Confirmed migration did not preserve legacy rule meaning.");
        await SaveLastSession(CaptureView());var stored=await settings!.Load<SavedView>("last-session.json");stored!.Filter.Validate();
        if(stored.Filter.SchemaVersion!=2||stored.Filter.DirectoryRules[0].BasePath!=source)throw new InvalidOperationException("Source context was lost on session save.");
        foreach(bool replaceOriginal in new[]{false,true})
        {
            await OpenRoot(source);await settings.Save("views.json",new Dictionary<string,SavedView>{{"legacy",legacy}});
            ContentDialog? dialog=null;TextBox? input=null;verifyCollectionRuleMigration=(opened,field)=>{dialog=opened;input=field;};
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Delay the old operation's completion even after navigation cancels
            // its token, so its late persistence is exercised deterministically.
            verifyCollectionRefreshBarrier=_=>{entered.TrySetResult();return release.Task;};
            Task applying=ApplyNamedView("legacy",legacy);
            try
            {
                await WaitUntil(()=>dialog?.IsLoaded==true,TimeSpan.FromSeconds(5));input!.Text=source;dialog!.UpdateLayout();
                ((IInvokeProvider)new ButtonAutomationPeer(InputDescendants(dialog).OfType<Button>().Single(button=>button.Name=="PrimaryButton")).GetPattern(PatternInterface.Invoke)).Invoke();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if(applying.IsCompleted)throw new InvalidOperationException("Migration fixture failed to delay collection refresh.");
                await OpenRoot(source);var other=CaptureView() with{Filter=CurrentFilter() with{NamePathQuery="new independent filter"}};
                await settings.Update<Dictionary<string,SavedView>>("views.json",latest=>{latest!["independent"]=other;if(replaceOriginal)latest["legacy"]=other;return latest;});
                release.TrySetResult();await applying.WaitAsync(TimeSpan.FromSeconds(10));
                var views=await settings.Load<Dictionary<string,SavedView>>("views.json");
                if(!SameSavedView(views!["independent"],other))throw new InvalidOperationException("Migration discarded an independently saved view.");
                if(replaceOriginal)
                {if(!SameSavedView(views["legacy"],other))throw new InvalidOperationException("Migration overwrote a newer replacement of the same view.");}
                else if(views["legacy"].Root!=legacy.Root||views["legacy"].Filter.CollectionId!=collection.Id||views["legacy"].Filter.DirectoryRules[0].BasePath!=source||views["legacy"].Filter.NamePathQuery!=legacy.Filter.NamePathQuery)
                    throw new InvalidOperationException("Migration saved another page's filter into the legacy named view.");
            }
            finally{release.TrySetResult();verifyCollectionRefreshBarrier=null;verifyCollectionRuleMigration=null;if(dialog?.IsLoaded==true)dialog.Hide();await applying;}
        }
        report["nameAndSourcePathRulesAgreeWithPickerAndPreview"]=true;report["legacyConfirmCancelAndSave"]=true;report["lateMigrationRetainsOriginalContextAndConcurrentSaves"]=true;report["status"]="PASS";
    }

    private void VerifyTreeCaseSelection(string source,Dictionary<string,object> report)
    {
        // Distinct spelling in the native tree projection. This assertion does
        // not pretend the host enabled NTFS case-sensitive directories.
        var upper=new TreeViewNode{Content=new FolderNode(Path.Combine(source,"A"),"A")};
        var lower=new TreeViewNode{Content=new FolderNode(Path.Combine(source,"a"),"a")};
        FolderTree.RootNodes.Add(upper);FolderTree.RootNodes.Add(lower);
        ShowTreeRoot(Path.Combine(source,"a"));
        if(!ReferenceEquals(activeTreeRoot,lower))throw new InvalidOperationException("An earlier folded match hid the exact lowercase tree node.");
        ShowTreeRoot(Path.Combine(source,"A"));
        if(!ReferenceEquals(activeTreeRoot,upper))throw new InvalidOperationException("Uppercase tree node was merged with its lowercase sibling.");
        report["nativeTreeExactSpelling"]=true;report["caseSensitiveFilesystem"]="NOT_RUN: enabling the isolated directory flag was denied by Windows";report["status"]="PASS";
    }

    private async Task VerifyExplicitFileOpen(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        ApplySavedFilter(CurrentFilter() with{FileExtensions=["jpg"],Formats=["jpeg"],Raw="only",IncludePending=true,Ranges=new(){["width"]=new(1000,null)}});
        await ApplyBrowserFilters();
        string first=Path.Combine(source,"A","image-03.png");await OpenPath(first);
        if(selected?.Name!="image-03.png"||fitBitmap is null)throw new InvalidOperationException("Explicit file open retained conflicting filters and failed to show its target.");
        report["explicitOpenClearsConflictingFilters"]=true;
        string next=Path.Combine(source,"B","first.png");File.Copy(first,next);File.Copy(first,Path.Combine(source,"B","second.png"));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyBackgroundScanCompletionBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
        Task opening=OpenPath(next);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(()=>selected?.Name=="first.png"&&fitBitmap is not null,TimeSpan.FromSeconds(5));
            if(opening.IsCompleted)throw new InvalidOperationException("Fixture failed to hold scan completion.");
            long second=await catalog!.FindOrdinal(resultHandle!.Id,"second.png",lifetime.Token)??throw new InvalidOperationException("Missing second file.");
            await SelectBrowserOrdinal(results!,(int)second,lifetime.Token);
            release.TrySetResult();await opening.WaitAsync(TimeSpan.FromSeconds(10));
            if(selected?.Name!="second.png")throw new InvalidOperationException("An older file open overrode the newer selection after scan completion.");
            report["targetShownBeforeScanCompletes"]=true;report["newerSelectionOwnsPreview"]=true;
        }
        finally{release.TrySetResult();verifyBackgroundScanCompletionBarrier=null;await opening;}
        string delayed=Path.Combine(source,"delayed");Directory.CreateDirectory(delayed);
        for(int i=0;i<300;i++)File.Copy(first,Path.Combine(delayed,$"file-{i:D3}.png"));
        foreach(string action in new[]{"replace","stop","fail"})
        {
            var pageEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pageRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            verifyFileOpenPageBarrier=async token=>{pageEntered.TrySetResult();await pageRelease.Task.WaitAsync(token);if(action=="fail")throw new IOException("Controlled target page failure.");};
            Task pending=OpenPath(Path.Combine(delayed,"file-299.png"));Task? replacement=null;
            try
            {
                await pageEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));verifyFileOpenPageBarrier=null;
                if(action=="replace")replacement=OpenPath(Path.Combine(delayed,"file-298.png"));
                if(action=="stop")CancelScan(this,new());
                pageRelease.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(15));
                if(replacement is not null)await replacement.WaitAsync(TimeSpan.FromSeconds(15));
                if(action=="replace"&&selected?.Name!="file-298.png"||action=="stop"&&selected is not null||
                    action=="fail"&&!Status.Text.StartsWith("操作未完成：",StringComparison.Ordinal))
                    throw new InvalidOperationException("Delayed file activation lost ownership or its error: "+action);
            }
            finally{pageRelease.TrySetResult();verifyFileOpenPageBarrier=null;await pending;if(replacement is not null)await replacement;}
        }
        report["delayedPageHonorsNewActivationStopAndError"]=true;
        string hidden=Path.Combine(source,"B","hidden.png");File.Copy(first,hidden);File.SetAttributes(hidden,FileAttributes.Hidden);
        try
        {
            ShowHidden.IsChecked=false;await OpenPath(hidden);
            if(selected is not null||ShowHidden.IsChecked==true||!QualityLabel.Text.Contains("隐藏",StringComparison.Ordinal))
                throw new InvalidOperationException("Hidden target was silently excluded or enabled unrelated hidden files.");
        }
        finally{File.SetAttributes(hidden,FileAttributes.Normal);}
        report["hiddenTargetGetsExplicitFeedback"]=true;
        report["status"]="PASS";
    }

    private async Task VerifyRotatedFitResolution(string source,Dictionary<string,object> report)
    {
        string image=Path.Combine(source,"A","rotated-fit.png");
        using(var fixture=new CanvasRenderTarget(ImageCanvas,1600,3600,96))
        {
            using(var draw=fixture.CreateDrawingSession())
            {draw.Clear(Microsoft.UI.Colors.White);for(int x=0;x<1600;x+=32)draw.FillRectangle(x,0,16,3600,Microsoft.UI.Colors.DarkBlue);}
            await fixture.SaveAsync(image,CanvasBitmapFileFormat.Png);
        }
        AppWindow.Resize(new(1100,1200));Shell.UpdateLayout();
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        long? ordinal=await catalog!.FindOrdinal(resultHandle!.Id,"A\\rotated-fit.png",lifetime.Token);
        if(ordinal is null)throw new InvalidOperationException("Rotation fixture was not indexed.");
        await SelectBrowserOrdinal(results!,(int)ordinal.Value,lifetime.Token);
        await WaitUntil(()=>!previewLoading&&!queryBusy,TimeSpan.FromSeconds(10));
        await RunViewerAction(ViewerAction.Fit);var before=fitBitmap!.SizeInPixels;
        await RunViewerAction(ViewerAction.Rotate);
        double demand=Math.Min(1,EffectiveScale()*Shell.XamlRoot.RasterizationScale);
        report["initialPixels"]=new{before.Width,before.Height};report["rotatedDemand"]=new{width=sourceWidth*demand,height=sourceHeight*demand};
        if(before.Width+2>=sourceWidth*demand)throw new InvalidOperationException("Rotation fixture did not increase pixel demand.");
        if(fitBitmap!.SizeInPixels.Width+2<sourceWidth*demand||fitBitmap.SizeInPixels.Height+2<sourceHeight*demand)
            throw new InvalidOperationException("Fit rotation enlarged the display without requesting enough decoded pixels.");
        report["rotationRequestsRequiredPixels"]=true;
        int decodes=0;verifyFitDecodeRequested=()=>decodes++;
        try
        {
            await RunViewerAction(ViewerAction.RotateCounterclockwise);
            if(decodes!=0)throw new InvalidOperationException("Smaller rotated demand decoded again.");
            var small=await previewWorker!.RequestPixels(SourcePath(selected!),"fit",Context(selected!,selection),new(256,256),selectionStop.Token,Stamp(selected!));
            await PresentFit(small,selection,selectionStop.Token);var retained=fitBitmap;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            verifyFitResponseBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            Task older=RunViewerAction(ViewerAction.Rotate);Task? newer=null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));newer=RunViewerAction(ViewerAction.Rotate);
                if(rotation!=2)throw new InvalidOperationException("Newer rotation was not applied immediately.");
                release.TrySetResult();await Task.WhenAll(older,newer).WaitAsync(TimeSpan.FromSeconds(10));
                if(rotation!=2||!ReferenceEquals(retained,fitBitmap))throw new InvalidOperationException("Old fit completion rolled back angle or replaced the retained bitmap.");
            }
            finally{release.TrySetResult();verifyFitResponseBarrier=null;await older;if(newer is not null)await newer;}
            int angle=rotation;
            for(int i=0;i<4;i++)await RunViewerAction(ViewerAction.Rotate);
            if(rotation!=angle)throw new InvalidOperationException("Four rotations did not preserve the latest angle.");
            var offlineBitmap=fitBitmap;int beforeOffline=decodes;offlinePreview=true;
            try{await RunViewerAction(ViewerAction.Rotate);}
            finally{offlinePreview=false;}
            if(decodes!=beforeOffline||!ReferenceEquals(offlineBitmap,fitBitmap))throw new InvalidOperationException("Offline rotation attempted to decode its missing source.");
            report["smallerDemandDoesNotDecode"]=true;report["newerAngleOwnsFitCompletion"]=true;report["fourRotationsAndOffline"]=true;
        }
        finally{verifyFitDecodeRequested=null;verifyFitResponseBarrier=null;}
        report["status"]="PASS";
    }

    private async Task VerifyDiagnosticPolicy(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);
        await WaitUntil(()=>!previewLoading&&!queryBusy,TimeSpan.FromSeconds(10));
        if(fitBitmap is null)throw new InvalidOperationException("Build-profile fixture did not present its image.");
        var accepted=resultHandle;verifyCandidateBarrier=_=>throw new IOException("DiagnosticPolicyFixture");
        try{await RefreshQuery();}finally{verifyCandidateBarrier=null;}
        if(!ReferenceEquals(accepted,resultHandle)||!Status.Text.StartsWith("操作未完成：",StringComparison.Ordinal))
            throw new InvalidOperationException("Query failure did not preserve results and show its user-visible error.");
        string queryFile=Path.Combine(RuntimeDataDirectory,"query-failures.jsonl");
        string scanFile=Path.Combine(dataDirectory,"logs","scan.jsonl");
        if(DevelopmentDiagnostics.Enabled)
        {
            await WaitUntil(()=>File.Exists(scanFile),TimeSpan.FromSeconds(5));
            if(!File.Exists(queryFile)||scanLog is null)throw new InvalidOperationException("Development diagnostics were not written.");
        }
        else if(Directory.Exists(Path.Combine(dataDirectory,"logs"))||File.Exists(queryFile)||webviewEvents.Count!=0||scanLog is not null)
            throw new InvalidOperationException("Production build collected diagnostic logs.");
        report["developmentDiagnostics"]=DevelopmentDiagnostics.Enabled;
        report["presentedImageAndRetainedResultsOnError"]=true;
        report["userErrorVisible"]=true;report["status"]="PASS";
    }

    private async Task VerifySavedViewAdmission(string source,Dictionary<string,object> report,bool late)
    {
        await OpenRoot(Path.Combine(source,"A"));if(metadataTask is not null)await metadataTask;
        ApplySavedFilter(CurrentFilter() with{FileExtensions=["png"],NamePathQuery="image"});await ApplyBrowserFilters();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);
        await WaitUntil(()=>!previewLoading&&!queryBusy,TimeSpan.FromSeconds(10));
        monitor?.Dispose();monitor=null;
        var previous=CaptureView();var oldHandle=resultHandle;var oldRow=selected;
        string oldRoot=root,oldId=rootId;long oldEpoch=epoch;
        var target=previous with{Root=Path.Combine(source,"B"),Filter=previous.Filter with{FileExtensions=["jpg"],NamePathQuery=""},Details=!previous.Details,SelectedPath="image-01.png"};
        if(late)verifyRootAdmissionBarrier=_=>throw new BrowsingBudgetException();
        else verifyRootPreflightBarrier=_=>throw new BrowsingBudgetException();
        try{await RestoreSavedView(target);}
        finally{verifyRootAdmissionBarrier=null;verifyRootPreflightBarrier=null;}
        report["refusalStage"]=late?"late-admission":"preflight";
        report["stateAfterRefusal"]=new{sameRoot=root==oldRoot,sameId=rootId==oldId,sameEpoch=epoch==oldEpoch,sameHandle=ReferenceEquals(oldHandle,resultHandle),sameSelection=ReferenceEquals(oldRow,selected),details=DetailsMode.IsChecked,extensions=CurrentFilter().FileExtensions};
        if(root!=oldRoot||rootId!=oldId||epoch!=oldEpoch||!ReferenceEquals(oldHandle,resultHandle)||!ReferenceEquals(oldRow,selected)
            ||DetailsMode.IsChecked!=previous.Details||!CurrentFilter().FileExtensions.SequenceEqual(previous.Filter.FileExtensions)||CurrentFilter().NamePathQuery!=previous.Filter.NamePathQuery)
            throw new InvalidOperationException("Rejected saved-view navigation changed accepted controls, selection or results.");
        await SaveLastSession(CaptureView());var saved=await settings!.Load<SavedView>("last-session.json");
        if(saved?.Root!=previous.Root||!saved.Filter.FileExtensions.SequenceEqual(previous.Filter.FileExtensions))throw new InvalidOperationException("Rejected target leaked into explicit view persistence.");
        report["rejectionPreservesAcceptedViewAndSave"]=true;
        // History must remain available when its target cannot be admitted,
        // even with an unfinished control draft in the displayed view.
        navigationHistory.VisitFrom(target);long historyBefore=navigationHistory.Revision;
        MinWidth.Text="-";verifyRootPreflightBarrier=_=>throw new BrowsingBudgetException();
        try{await NavigateHistory(false);}
        finally{verifyRootPreflightBarrier=null;}
        if(navigationHistory.Revision!=historyBefore||navigationHistory.PeekBack()!=target||root!=oldRoot)
            throw new InvalidOperationException("Rejected history navigation consumed history or changed the accepted root.");
        await RestoreSavedView(previous,false);
        if(root!=oldRoot||CurrentFilter().Ranges.Count!=previous.Filter.Ranges.Count)
            throw new InvalidOperationException("Unsubmitted draft blocked a valid saved view.");
        report["historyAndRestoreIgnoreUnsubmittedDraft"]=true;
        foreach(bool newerRefused in new[]{false,true})
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
            verifyRootPreflightBarrier=async token=>
            {
                if(++calls>1)throw new BrowsingBudgetException();
                entered.TrySetResult();await release.Task.WaitAsync(token);
            };
            Task older=RestoreSavedView(target,false);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await RestoreSavedView(newerRefused?target:previous,false);
                var acceptedAfterNewer=CaptureAcceptedView();
                release.TrySetResult();await older.WaitAsync(TimeSpan.FromSeconds(15));
                if(root!=oldRoot||DetailsMode.IsChecked!=previous.Details||pendingViewRestore?.View.Root==target.Root||
                    !CurrentFilter().FileExtensions.SequenceEqual(acceptedAfterNewer!.Filter.FileExtensions))
                    throw new InvalidOperationException("An older pending restore replaced a newer navigation intent.");
            }
            finally{release.TrySetResult();verifyRootPreflightBarrier=null;await older;}
        }
        report["newerSameRootAndRejectedRestoreSupersedeOlderPreflight"]=true;
        foreach(bool newerRefused in new[]{true,false})
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var newerEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var newerRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            verifySameRootRestoreBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            verifyRootPreflightBarrier=async token=>{newerEntered.TrySetResult();await newerRelease.Task.WaitAsync(token);if(newerRefused)throw new BrowsingBudgetException();};
            Task older=RestoreSavedView(previous with{Filter=previous.Filter with{FileExtensions=["jpg"]},Details=!previous.Details},false,recheckDirectory:true);
            Task? newer=null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));newer=OpenRoot(target.Root);
                await newerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));release.TrySetResult();await older.WaitAsync(TimeSpan.FromSeconds(10));
                if(root!=oldRoot||DetailsMode.IsChecked!=previous.Details||!CurrentFilter().FileExtensions.SequenceEqual(previous.Filter.FileExtensions))
                    throw new InvalidOperationException("Older same-root restore modified controls while a newer navigation was in preflight.");
                newerRelease.TrySetResult();await newer.WaitAsync(TimeSpan.FromSeconds(15));
                if(root!=(newerRefused?oldRoot:target.Root)||DetailsMode.IsChecked!=previous.Details||!CurrentFilter().FileExtensions.SequenceEqual(previous.Filter.FileExtensions))
                    throw new InvalidOperationException("Newer ordinary navigation inherited controls from an older restore.");
            }
            finally{release.TrySetResult();newerRelease.TrySetResult();verifySameRootRestoreBarrier=null;verifyRootPreflightBarrier=null;await older;if(newer is not null)await newer;}
        }
        report["newerNavigationSupersedesOlderSameRootRestore"]=true;report["status"]="PASS";
    }

    private async Task VerifyAuditParentNavigation(string source,Dictionary<string,object> report)
    {
        if(rootId.Length!=0)throw new InvalidOperationException("Parent regression requires a fresh isolated data directory.");
        UpdateCommandAvailability();
        if(CanNavigateParent||ParentButton.IsEnabled)throw new InvalidOperationException("Empty-root parent command is enabled.");
        await NavigateParent();await RunViewerAction(ViewerAction.ParentFolder);
        report["emptyRootButtonAndKeyboardGuard"]=true;
        if(BrowserEmptyDetail.Text.Contains("拖"))throw new InvalidOperationException("Empty view advertises unsupported folder drop navigation.");
        report["emptyViewUsesExistingOpenEntry"]=true;
        string child=Path.Combine(source,"A");await OpenRoot(child);
        if(!CanNavigateParent)throw new InvalidOperationException("Valid parent command is disabled.");
        await NavigateParent();
        if(!string.Equals(root,source,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Parent navigation selected another directory.");
        bool ready=controlsReady;controlsReady=false;
        try{if(CanNavigateParent)throw new InvalidOperationException("Unready command enabled.");await NavigateParent();}
        finally{controlsReady=ready;}
        var collection=await catalog!.CreateCollection("isolated-parent",lifetime.Token);
        await RefreshCollectionsTree();
        await OpenCollection(collection.Id);
        if(!CanNavigateParent)throw new InvalidOperationException("Collection management parent entry was removed.");
        await DeleteCollectionAndRefresh(collection.Id);
        if(CanNavigateParent||rootId.Length!=0)throw new InvalidOperationException("Deleted collection retained parent navigation.");
        await NavigateParent();await RunViewerAction(ViewerAction.ParentFolder);
        report["normalParentAndDeletedCollection"]=true;
        report["status"]="PASS";
    }

    private async Task VerifyAdvancedSubmission(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        var initial=CurrentFilter();
        async Task Submit(long? min,long? max,long? height,bool cancel=false,bool keepDraft=false)
        {
            AdvancedFilterEditor? draft=null;verifyAdvancedFilterDraft=editor=>draft=editor;
            Task editing=EditAdvancedFilters();
            try
            {
                await WaitUntil(()=>draft is not null&&advancedFilterDialog?.IsLoaded==true,TimeSpan.FromSeconds(5));
                if(!keepDraft)
                {
                    draft!.Ranges["width"].Min.Text=min?.ToString(CultureInfo.CurrentCulture)??"";
                    draft.Ranges["width"].Max.Text=max?.ToString(CultureInfo.CurrentCulture)??"";
                    draft.Ranges["height"].Min.Text=height?.ToString(CultureInfo.CurrentCulture)??"";
                }
                var dialog=advancedFilterDialog!;dialog.UpdateLayout();
                var button=InputDescendants(dialog).OfType<Button>().Single(item=>item.Name==(cancel?"CloseButton":"PrimaryButton"));
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await editing.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally{verifyAdvancedFilterDraft=null;if(advancedFilterDialog is {} open)open.Hide();await editing;}
        }
        await Submit(32,128,24);
        var actual=CurrentFilter();
        if(actual.Ranges.GetValueOrDefault("width")!=new IntRange(32,128)||actual.Ranges.GetValueOrDefault("height")!=new IntRange(24,null))
            throw new InvalidOperationException("Advanced dialog Primary submission lost width/height minima before query.");
        if(metadataTask is not null)await metadataTask;await RefreshQuery();
        if(resultHandle?.Count!=12)throw new InvalidOperationException("Advanced range result does not match the 64x48 fixture.");
        await WaitUntil(()=>!queryBusy&&!browserMovementPending,TimeSpan.FromSeconds(5));
        foreach(string draft in new[]{"-","","9223372036854775808"})
        {
            MinWidth.Text=draft;long request=queryRequest;
            await RefreshQuery(preserveViewport:true,scanPreview:true);
            if(queryRequest<=request||resultHandle?.Count!=12||browserEmptyError is not null||lastAppliedFilter?.Ranges.GetValueOrDefault("width")!=new IntRange(32,128))
                throw new InvalidOperationException("Background publication consumed an unsubmitted dimension draft: "+draft);
        }
        MinWidth.Text="";await ApplyBrowserFilters();
        if(lastAppliedFilter?.Ranges.GetValueOrDefault("width")!=new IntRange(null,128))throw new InvalidOperationException("Explicit quick-field clear did not submit.");
        report["backgroundIgnoresDraftAndExplicitClearCommits"]=true;
        await Submit(9007199254740993,long.MaxValue,long.MaxValue);
        actual=CurrentFilter();
        if(actual.Ranges["width"]!=new IntRange(9007199254740993,long.MaxValue)||actual.Ranges["height"].Min!=long.MaxValue)
            throw new InvalidOperationException("Advanced submission rounded an Int64 boundary.");
        await Submit(null,128,null,true);
        if(CurrentFilter().Ranges["width"]!=actual.Ranges["width"])throw new InvalidOperationException("Cancel changed applied ranges.");
        ApplySavedFilter(actual);
        if(CurrentFilter().Ranges["width"]!=actual.Ranges["width"])throw new InvalidOperationException("Saved filter restoration rounded an Int64 boundary.");
        await Submit(null,null,null,keepDraft:true);
        if(CurrentFilter().Ranges["width"]!=actual.Ranges["width"])throw new InvalidOperationException("An unedited advanced submission changed an Int64 boundary.");
        report["dialogSubmissionAndInt64Restore"]=true;
        ApplySavedFilter(initial with{Ranges=new(){["width"]=new(32,null)}});await RefreshQuery();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyRootAdmissionBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
        string target=Path.Combine(source,"B");Task opening=OpenRoot(target);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));MinWidth.Text="-";release.TrySetResult();
            await opening.WaitAsync(TimeSpan.FromSeconds(15));
            if(root!=target||browserEmptyError is not null||lastAppliedFilter?.Ranges.GetValueOrDefault("width")!=new IntRange(32,null))
                throw new InvalidOperationException("Root admission reread a draft changed after navigation began.");
            report["rootAdmissionUsesCapturedFilter"]=true;
        }
        finally{release.TrySetResult();verifyRootAdmissionBarrier=null;await opening;}
        ApplySavedFilter(initial);await OpenRoot(source);await RefreshQuery();
        report["status"]="PASS";
    }
}
