using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Func<CancellationToken,Task>? verifyTextWindowReadBarrier;

    private async Task VerifyTextNavigationOwnership(string source,Dictionary<string,object> report)
    {
        await File.WriteAllTextAsync(Path.Combine(source,"navigation.txt"),string.Concat(Enumerable.Range(1,3000).Select(line=>$"Navigation line {line:D4} - current text viewport.\n")));
        suppressFilters=true;try{SelectTag(Category,"all");}finally{suppressFilters=false;}
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;await RefreshQuery();
        long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,"navigation.txt",lifetime.Token)??throw new InvalidOperationException("Text navigation fixture is missing.");
        var row=(FileRow)results![checked((int)ordinal)]!;await results.EnsureLoaded(row,lifetime.Token);await SelectPreview(row);
        var failures=new List<string>();report["errors"]=failures;
        var session=textSessionStop;long sessionVersion=textSessionGeneration;
        try
        {
            foreach(bool pageNavigation in new[]{false,true})
            {
                await LoadText(0,selection,selectionStop.Token);
                if(pageNavigation){TextScroll.UpdateLayout();TextScroll.ChangeView(null,TextScroll.ScrollableHeight,null,true);await Task.Delay(30);}
                var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken retiredToken=default;
                verifyTextFindLineBarrier=async token=>{retiredToken=token;entered.TrySetResult();await release.Task;};
                TextLineInput.Value=1;Task pending=JumpToTextLine();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));verifyTextFindLineBarrier=null;
                    if(pageNavigation)await PageReader(1);else{TextLineInput.Value=1200;await JumpToTextLine();}
                    long expectedStart=textStart;string expectedQuality=QualityLabel.Text;
                    bool targetShown=expectedStart>0;
                    release.TrySetResult();await pending.WaitAsync(TimeSpan.FromSeconds(5));
                    bool preserved=targetShown&&textStart==expectedStart&&QualityLabel.Text==expectedQuality;
                    report[pageNavigation?"lateLineVersusPage":"lateLineVersusLine"]=new{tokenCancelled=retiredToken.IsCancellationRequested,targetShown,preserved,expectedStart,textStart,quality=QualityLabel.Text};
                    if(!retiredToken.IsCancellationRequested||!preserved)failures.Add(pageNavigation?"A pending line lookup overrode a newer page navigation.":"A pending line lookup overrode a newer line navigation.");
                }
                finally{release.TrySetResult();verifyTextFindLineBarrier=null;await pending;}
            }

            await LoadText(0,selection,selectionStop.Token);
            var resultRow=new TextSearchRow(new TextMatch(0,10,"Navigation"),"Navigation",textSearchResultsRevision);
            var readEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken retiredReadToken=default;
            verifyTextWindowReadBarrier=async token=>{retiredReadToken=token;readEntered.TrySetResult();await readRelease.Task;};
            Task resultOpen=OpenTextSearchResultCore(resultRow);
            try
            {
                await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));verifyTextWindowReadBarrier=null;
                await LoadText(65536,selection,selectionStop.Token);
                long expectedStart=textStart;string expectedQuality=QualityLabel.Text;
                readRelease.TrySetResult();await resultOpen.WaitAsync(TimeSpan.FromSeconds(3));
                bool preserved=textStart==expectedStart&&QualityLabel.Text==expectedQuality&&TextContent.SelectionLength==0;
                report["lateSearchResultVersusWindow"]=new{tokenCancelled=retiredReadToken.IsCancellationRequested,preserved,expectedStart,textStart,quality=QualityLabel.Text};
                if(!retiredReadToken.IsCancellationRequested||!preserved)failures.Add("A replaced search-result window applied its old highlight/status to the newer text window.");
            }
            finally{readRelease.TrySetResult();verifyTextWindowReadBarrier=null;await resultOpen;}

            TextLineInput.Value=1200;await JumpToTextLine();
            bool lineWorks=textStart>0&&QualityLabel.Text.Contains($"第 {1200:N0} 行",StringComparison.Ordinal);
            await OpenTextSearchResultCore(resultRow);
            bool resultWorks=textStart==0&&TextContent.SelectedText=="Navigation"&&QualityLabel.Text.StartsWith("搜索结果",StringComparison.Ordinal);
            TextScroll.UpdateLayout();TextScroll.ChangeView(null,TextScroll.ScrollableHeight,null,true);await Task.Delay(30);
            long expectedNext=textNext;await PageReader(1);
            bool pageWorks=textStart==expectedNext;
            bool sessionAlive=ReferenceEquals(session,textSessionStop)&&!session.IsCancellationRequested&&sessionVersion==textSessionGeneration;
            report["currentNavigationStillWorks"]=new{lineWorks,resultWorks,pageWorks,sessionAlive};
            if(!lineWorks||!resultWorks||!pageWorks||!sessionAlive)failures.Add("Current text navigation failed or cancelled the independent document/index session.");
        }
        finally{verifyTextFindLineBarrier=null;verifyTextWindowReadBarrier=null;}
        if(failures.Count>0)throw new InvalidOperationException(string.Join(" ",failures));
        report["status"]="PASS";
    }
}
