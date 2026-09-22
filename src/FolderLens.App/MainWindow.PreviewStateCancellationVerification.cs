using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<CancellationToken,Task>? verifyTextWindowBarrier;
    private Func<CancellationToken,Task>? verifyMarkdownNavigationBarrier;
    private Func<CancellationToken,Task>? verifyTextFindLineBarrier,verifyTextFindNextBarrier;
    private Func<Task>? verifyMarkdownTextSizeBarrier;
    private Action<Microsoft.UI.Xaml.Controls.WebView2>? verifyMarkdownViewCreated;

    private async Task VerifyPreviewStateCancellation(string source,Dictionary<string,object> report)
    {
        await File.WriteAllTextAsync(Path.Combine(source,"leave.txt"),"Plain text must remain usable after cancelling its rendered presentation.\n");
        await File.WriteAllTextAsync(Path.Combine(source,"mode.md"),"# Mode cancellation\n\n![image](A/image-00.png)\n\nThe original document remains available.\n");
        suppressFilters=true;try{SelectTag(Category,"all");}finally{suppressFilters=false;}
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var errors=new List<string>();report["errors"]=errors;
        var viewEvents=new List<object>();report["viewLifecycle"]=viewEvents;int viewId=0;
        verifyMarkdownViewCreated=view=>
        {
            int id=++viewId;
            void Note(string stage,Exception? error=null)=>viewEvents.Add(new{id,stage,time=DateTimeOffset.UtcNow,error=error?.GetType().Name,hresult=error is null?null:$"0x{error.HResult:X8}",message=error?.Message,view.IsLoaded,view.ActualWidth,view.ActualHeight,hostVisibility=MarkdownHost.Visibility.ToString(),hostWidth=MarkdownHost.ActualWidth,hostHeight=MarkdownHost.ActualHeight,previewHeight=PreviewPane.ActualHeight,surfaceHeight=PreviewSurface.ActualHeight,textHeight=TextScroll.ActualHeight,extrasHeight=PreviewExtras.ActualHeight,presentationRequested=MarkdownPresentationRequested,selection,resources=markdownResourceRequests});
            Note("created");view.Loaded+=(_,_)=>Note("loaded");view.Unloaded+=(_,_)=>Note("unloaded");view.SizeChanged+=(_,_)=>Note("size");view.CoreWebView2Initialized+=(_,args)=>Note("controller-initialized",args.Exception);
        };
        void Stage(string value)=>File.WriteAllText(Path.Combine(dataDirectory,"preview-state-stage.txt"),value);
        async Task<FileRow> Row(string path)
        {
            long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,path,lifetime.Token)??throw new InvalidOperationException("Missing fixture: "+path);
            var row=(FileRow)results![checked((int)ordinal)]!;await results.EnsureLoaded(row,lifetime.Token);return row;
        }
        var plain=await Row("leave.txt");var image=await Row("A\\image-00.png");var document=await Row("mode.md");
        try
        {
            Stage("late-text-jump");
            await SelectPreview(plain);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken retiredTextToken=default;
            verifyTextWindowBarrier=async token=>
            {
                retiredTextToken=token;entered.TrySetResult();
                try{await release.Task;token.ThrowIfCancellationRequested();}
                finally{finished.TrySetResult();}
            };
            try
            {
                TextOffset.Value=0;TextJump(TextOffset,new RoutedEventArgs());
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await SelectPreview(image);string expectedQuality=QualityLabel.Text;
                release.TrySetResult();await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));await Task.Delay(100);
                bool intact=ReferenceEquals(selected,image)&&fitBitmap is not null&&previewFailure is null&&QualityLabel.Text==expectedQuality;
                report["cancelledTextJump"]=new{tokenCancelled=retiredTextToken.IsCancellationRequested,newPreviewIntact=intact,quality=QualityLabel.Text};
                if(!retiredTextToken.IsCancellationRequested||!intact)errors.Add("A cancelled text jump changed the newly selected image's preview state.");
            }
            finally{release.TrySetResult();verifyTextWindowBarrier=null;}

            Stage("hidden-markdown-resource");
            entered=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken retiredResourceToken=default;
            verifyMarkdownResourceBarrier=async(_,token)=>{retiredResourceToken=token;entered.TrySetResult();await release.Task.WaitAsync(token);};
            try
            {
                await SelectPreview(document);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if(Environment.GetCommandLineArgs().Contains("--verify-markdown-resource-settled"))
                {
                    release.TrySetResult();await WaitUntil(()=>markdownResourceRequests==0,TimeSpan.FromSeconds(5));
                    report["resourceCompletedBeforeModeChange"]=true;
                }
                var textSession=textSessionStop;long textRevision=textSessionGeneration;
                ToggleMarkdown(ReaderRenderMode,new RoutedEventArgs());await Task.Delay(150);
                bool originalAlive=ReferenceEquals(textSession,textSessionStop)&&!textSession.IsCancellationRequested&&textRevision==textSessionGeneration&&TextScroll.Visibility==Visibility.Visible;
                bool retired=retiredResourceToken.IsCancellationRequested;
                report["hiddenMarkdownResource"]=new{tokenCancelled=retired,originalTextSessionAlive=originalAlive,requests=markdownResourceRequests};
                if(!retired||!originalAlive)errors.Add("Switching to the original text did not cancel hidden Markdown resource work independently of the text session.");
            }
            finally{release.TrySetResult();verifyMarkdownResourceBarrier=null;}
            await WaitUntil(()=>markdownResourceRequests==0,TimeSpan.FromSeconds(10));

            Stage("late-markdown-navigation");
            await SelectPreview(plain);await DisposeMarkdownView();
            entered=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken retiredNavigationToken=default;
            verifyMarkdownNavigationBarrier=async token=>{retiredNavigationToken=token;entered.TrySetResult();await release.Task;};
            Task pending=SelectPreview(document);
            try
            {
                await Task.WhenAny(entered.Task,pending).WaitAsync(TimeSpan.FromSeconds(8));
                if(!entered.Task.IsCompleted)
                {
                    report["lateMarkdownNavigationSetup"]=new{pendingStatus=pending.Status.ToString(),quality=QualityLabel.Text,markdownLoading,presentationRequested=MarkdownPresentationRequested,hostVisibility=MarkdownHost.Visibility.ToString(),hostWidth=MarkdownHost.ActualWidth,hostHeight=MarkdownHost.ActualHeight,webviewEvents=webviewEvents.ToArray()};
                    throw new InvalidOperationException("Markdown ended before reaching the navigation barrier: "+QualityLabel.Text);
                }
                if(MarkdownHost.Visibility!=Visibility.Visible)throw new InvalidOperationException("Markdown did not enter its native loading presentation.");
                var textSession=textSessionStop;long textRevision=textSessionGeneration;
                ToggleMarkdown(ReaderRenderMode,new RoutedEventArgs());release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(8));
                bool originalVisible=MarkdownHost.Visibility==Visibility.Collapsed&&TextScroll.Visibility==Visibility.Visible;
                bool originalAlive=ReferenceEquals(textSession,textSessionStop)&&!textSession.IsCancellationRequested&&textRevision==textSessionGeneration;
                report["lateMarkdownNavigation"]=new{tokenCancelled=retiredNavigationToken.IsCancellationRequested,originalVisible,originalTextSessionAlive=originalAlive};
                if(!retiredNavigationToken.IsCancellationRequested||!originalVisible||!originalAlive)errors.Add("Retired Markdown navigation overrode the user's switch back to the original text.");
            }
            finally{release.TrySetResult();verifyMarkdownNavigationBarrier=null;await pending;}

            Stage("late-original-text-window");
            await SelectPreview(document);ToggleMarkdown(ReaderRenderMode,new RoutedEventArgs());
            entered=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken retiredWindowToken=default;
            verifyTextWindowBarrier=async token=>{retiredWindowToken=token;entered.TrySetResult();await release.Task;};
            Task pendingWindow=LoadText(0,selection,selectionStop.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var textSession=textSessionStop;long textRevision=textSessionGeneration;
                await LoadMarkdown(selection,selectionStop.Token);
                release.TrySetResult();
                try{await pendingWindow.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}
                bool renderedVisible=MarkdownHost.Visibility==Visibility.Visible&&TextScroll.Visibility==Visibility.Collapsed;
                bool originalAlive=ReferenceEquals(textSession,textSessionStop)&&!textSession.IsCancellationRequested&&textRevision==textSessionGeneration;
                report["lateOriginalTextWindow"]=new{tokenCancelled=retiredWindowToken.IsCancellationRequested,renderedVisible,originalTextSessionAlive=originalAlive};
                if(!retiredWindowToken.IsCancellationRequested||!renderedVisible||!originalAlive)errors.Add("An older original-text window read overrode the newly requested Markdown presentation.");
            }
            finally{release.TrySetResult();verifyTextWindowBarrier=null;try{await pendingWindow;}catch(OperationCanceledException){}}

            async Task CheckRetiredLookup(bool lineLookup)
            {
                Stage(lineLookup?"late-line-lookup":"late-text-search");
                await LoadMarkdown(selection,selectionStop.Token);ToggleMarkdown(ReaderRenderMode,new RoutedEventArgs());
                var lookupEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var lookupRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken retiredLookupToken=default;
                Task Barrier(CancellationToken token){retiredLookupToken=token;lookupEntered.TrySetResult();return lookupRelease.Task;}
                TextLineInput.Value=1;TextQuery.Text="Mode cancellation";
                if(lineLookup)verifyTextFindLineBarrier=Barrier;else verifyTextFindNextBarrier=Barrier;
                var textSession=textSessionStop;long textRevision=textSessionGeneration;
                Task lookup=lineLookup?JumpToTextLine():FindText();
                try
                {
                    await lookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await LoadMarkdown(selection,selectionStop.Token);string expectedQuality=QualityLabel.Text;
                    lookupRelease.TrySetResult();await lookup.WaitAsync(TimeSpan.FromSeconds(5));
                    bool renderedVisible=MarkdownHost.Visibility==Visibility.Visible&&TextScroll.Visibility==Visibility.Collapsed;
                    bool originalAlive=ReferenceEquals(textSession,textSessionStop)&&!textSession.IsCancellationRequested&&textRevision==textSessionGeneration;
                    bool currentStatus=QualityLabel.Text==expectedQuality;
                    report[lineLookup?"lateLineLookup":"lateTextSearch"]=new{tokenCancelled=retiredLookupToken.IsCancellationRequested,renderedVisible,originalTextSessionAlive=originalAlive,currentStatus,quality=QualityLabel.Text};
                    if(!retiredLookupToken.IsCancellationRequested||!renderedVisible||!originalAlive||!currentStatus)errors.Add(lineLookup?"An older line lookup overrode the newly requested Markdown presentation.":"An older text search overrode the newly requested Markdown presentation.");
                }
                finally{lookupRelease.TrySetResult();verifyTextFindLineBarrier=null;verifyTextFindNextBarrier=null;await lookup;}
            }
            await CheckRetiredLookup(true);await CheckRetiredLookup(false);

            Stage("current-modes-still-work");
            await SelectPreview(plain);await SelectPreview(document);
            bool canRender=MarkdownHost.Visibility==Visibility.Visible&&markdown?.CoreWebView2 is not null;
            ToggleMarkdown(ReaderRenderMode,new RoutedEventArgs());
            await LoadText(0,selection,selectionStop.Token);
            bool canRead=TextScroll.Visibility==Visibility.Visible&&TextContent.Text.Contains("Mode cancellation");
            report["currentModesStillWork"]=new{canRender,canRead};
            if(!canRender||!canRead)errors.Add("The current document could not render or return to its original text.");
            TextLineInput.Value=1;await JumpToTextLine();
            bool currentLine=TextScroll.Visibility==Visibility.Visible&&QualityLabel.Text.Contains("第 1 行");
            TextQuery.Text="Mode cancellation";await FindText();
            bool currentSearch=TextScroll.Visibility==Visibility.Visible&&TextContent.SelectedText.Contains("Mode cancellation");
            report["currentLookupsStillWork"]=new{currentLine,currentSearch};
            if(!currentLine||!currentSearch)errors.Add("The current line lookup or text search did not show its requested result.");

            Stage("retired-search-error-in-original-text");
            await LoadText(0,selection,selectionStop.Token);
            TextQuery.Text="Mode cancellation";
            var searchEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var searchRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            verifyTextFindNextBarrier=async _=>{searchEntered.TrySetResult();await searchRelease.Task;throw new IOException("Retired text search failure.");};
            Task retiredSearch=SearchNextText();
            try
            {
                await searchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));verifyTextFindNextBarrier=null;
                TextQuery.Text="original document";await SearchNextText();
                string expectedQuality=QualityLabel.Text;
                bool currentMatch=TextContent.SelectedText=="original document";
                searchRelease.TrySetResult();await retiredSearch.WaitAsync(TimeSpan.FromSeconds(3));
                bool currentStatus=QualityLabel.Text==expectedQuality&&previewFailure is null;
                report["retiredSearchErrorInOriginalText"]=new{currentMatch,currentStatus,quality=QualityLabel.Text};
                if(!currentMatch||!currentStatus)errors.Add("A replaced text search's error overwrote the current search result in the same original-text presentation.");
            }
            finally{searchRelease.TrySetResult();await retiredSearch;verifyTextFindNextBarrier=null;}
            Stage("current-text-search-error");
            await LoadText(0,selection,selectionStop.Token);
            try
            {
                verifyTextFindNextBarrier=_=>Task.FromException(new IOException("Current text search failure."));
                await SearchNextText();
                bool reported=previewFailure is not null&&QualityLabel.Text==previewFailure;
                report["currentTextSearchErrorReported"]=reported;
                if(!reported)errors.Add("The current text search error was swallowed.");
            }
            finally{verifyTextFindNextBarrier=null;}
            await LoadText(0,selection,selectionStop.Token);

            async Task CheckRetiredFontError(bool leaveDocument)
            {
                Stage(leaveDocument?"late-reader-font-selection-error":"late-reader-font-mode-error");
                await SelectPreview(document);
                if(MarkdownHost.Visibility!=Visibility.Visible||markdown?.CoreWebView2 is null)throw new InvalidOperationException("Font error test requires the current Markdown view.");
                double originalSize=TextContent.FontSize;
                var fontEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var fontRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                verifyMarkdownTextSizeBarrier=async()=>{fontEntered.TrySetResult();await fontRelease.Task;throw new IOException("Retired reader font failure.");};
                Task resize=ResizeReaderFont(2);
                try
                {
                    await fontEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if(leaveDocument)await SelectPreview(image);else ToggleMarkdown(ReaderRenderMode,new RoutedEventArgs());
                    string expectedQuality=QualityLabel.Text;
                    fontRelease.TrySetResult();await resize.WaitAsync(TimeSpan.FromSeconds(3));
                    bool currentStatus=QualityLabel.Text==expectedQuality;
                    bool currentPreview=leaveDocument?ReferenceEquals(selected,image)&&fitBitmap is not null&&previewFailure is null:
                        ReferenceEquals(selected,document)&&MarkdownHost.Visibility==Visibility.Collapsed&&TextScroll.Visibility==Visibility.Visible;
                    report[leaveDocument?"lateReaderFontSelectionError":"lateReaderFontModeError"]=new{currentStatus,currentPreview,quality=QualityLabel.Text};
                    if(!currentStatus||!currentPreview)errors.Add(leaveDocument?"An older reader font error changed the newly selected preview.":"An older reader font error changed the newly requested original-text presentation.");
                }
                finally{fontRelease.TrySetResult();await resize;verifyMarkdownTextSizeBarrier=null;TextContent.FontSize=originalSize;}
            }
            await CheckRetiredFontError(true);await CheckRetiredFontError(false);
            Stage("current-reader-font-error");
            await LoadMarkdown(selection,selectionStop.Token);
            double currentFontSize=TextContent.FontSize;
            try
            {
                verifyMarkdownTextSizeBarrier=()=>Task.FromException(new IOException("Current reader font failure."));
                await ResizeReaderFont(2);
                bool reported=QualityLabel.Text.StartsWith("无法调整排版字号：",StringComparison.Ordinal);
                report["currentReaderFontErrorReported"]=reported;
                if(!reported)errors.Add("The current reader font error was swallowed.");
            }
            finally{verifyMarkdownTextSizeBarrier=null;TextContent.FontSize=currentFontSize;}

            TextOffset.Value=double.NaN;TextJump(TextOffset,new RoutedEventArgs());
            report["currentTextJumpErrorReported"]=previewFailure is not null;
            if(previewFailure is null)errors.Add("The current text jump's invalid offset error was swallowed.");
            await LoadText(0,selection,selectionStop.Token);
            if(errors.Count>0)throw new InvalidOperationException(string.Join(" ",errors));
            report["status"]="PASS";Stage("complete");
        }
        finally{verifyTextWindowBarrier=null;verifyMarkdownResourceBarrier=null;verifyMarkdownNavigationBarrier=null;verifyMarkdownViewCreated=null;verifyTextFindLineBarrier=null;verifyTextFindNextBarrier=null;verifyMarkdownTextSizeBarrier=null;}
    }
}
