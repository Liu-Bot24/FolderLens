using FolderLens.Core;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFunctionalCount(string source,Dictionary<string,object> report,bool fail)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));source=Path.Combine(source,"functional-count");
        foreach(string group in new[]{"A","B"})
        {
            string directory=Path.Combine(source,group);Directory.CreateDirectory(directory);
            int count=group=="A"?300:20;string prefix=group=="A"?"z":"a";
            for(int i=0;i<count;i++)await File.WriteAllBytesAsync(Path.Combine(directory,$"{prefix}-{i:D3}.png"),sample);
        }
        folderGrouping=new(true,"first","logicalBytes","desc","all");sortDescending=false;SelectTag(SortField,"name");
        await OpenRoot(source);await SettleFunctionalCollection();var retired=RetireBrowserResults();if(retired is not null)await catalog!.ReleaseSnapshot(retired);
        FileRow? row=null;object? bitmap=null;long preview=-1;bool pendingHonest=false;
        verifyFirstPageBarrier=async _=>
        {
            verifyFirstPageBarrier=null;row=firstPageSequence[0];ActiveBrowser.SelectedItem=row;
            await WaitUntil(()=>ReferenceEquals(selected,row)&&!previewLoading&&selectedProperties?.Details is {State:not "notRequested"},TimeSpan.FromSeconds(8));
            bitmap=fitBitmap;preview=selection;
            string caption=viewerPosition?.Text??"";pendingHonest=caption.Contains("已加载")&&caption.Contains("整理中")&&!caption.Contains("/ 0");
            report["firstPage"]=new{caption,loaded=firstPageSequence.Length,fullHandle=resultHandle?.Id,exif=selectedProperties?.Details?.State,pendingHonest};
        };
        if(fail)verifyCandidateBarrier=_=>throw new IOException("FunctionalCountFixtureFailure");
        try{await RefreshQuery();}finally{verifyFirstPageBarrier=null;verifyCandidateBarrier=null;}
        if(fail)
        {
            string caption=viewerPosition?.Text??"";bool honest=caption.Contains("尚未就绪")&&!caption.Contains("/ 0");report["failedCaption"]=caption;
            if(!honest)report["failureStatusMissing"]=true;
            await RefreshQuery(scanPreview:true);
        }
        string expected=$"{selected!.Ordinal+1:N0} / {resultHandle!.Count:N0}";string actual=viewerPosition?.Text??"";
        bool updated=actual==expected;bool preserved=ReferenceEquals(selected,row)&&ReferenceEquals(fitBitmap,bitmap)&&selection==preview;
        report["complete"]=new{expected,actual,updated,preserved,total=resultHandle.Count,ordinal=selected.Ordinal};
        Search.Text="no-match-count-fixture";await ApplyBrowserFilters();
        bool empty=resultHandle?.Count==0&&selected is null&&viewerPosition?.Text=="";report["emptyCleared"]=empty;
        if(!pendingHonest||!updated||!preserved||!empty||report.ContainsKey("failureStatusMissing"))throw new InvalidOperationException("First-page viewer position was not refreshed honestly without reselecting or decoding.");report["status"]="PASS";
    }
}
