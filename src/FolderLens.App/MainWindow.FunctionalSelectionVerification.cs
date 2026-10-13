using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFunctionalSelection(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        source=Path.Combine(source,"functional-selection");
        for(int group=0;group<3;group++)
        {
            string directory=Path.Combine(source,$"group-{group}");Directory.CreateDirectory(directory);
            for(int index=0;index<200;index++)await File.WriteAllBytesAsync(Path.Combine(directory,$"image-{group*200+index:D3}.png"),sample);
        }
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;if(queryBusy)await queryCompletion;
        monitor?.Dispose();monitor=null;
        var checks=new List<object>();var failures=new List<string>();
        async Task<string[]> Ids()
        {
            var ranges=SelectedOrdinals(ActiveBrowser);
            var ids=new List<string>();
            for(long start=0;start<resultHandle!.Count;start+=256)
            {
                var page=await catalog!.ReadPage(resultHandle.Id,start,256,lifetime.Token);
                ids.AddRange(page.Where(item=>ranges.Any(range=>item.Ordinal>=range.Start&&item.Ordinal<range.End)).Select(item=>item.EntryId));
            }
            return ids.Order(StringComparer.Ordinal).ToArray();
        }
        foreach(bool details in new[]{false,true})foreach(bool grouped in new[]{false,true})foreach(bool continuous in new[]{false,true})
        {
            bool previous=suppressFilters;suppressFilters=true;
            try{folderGrouping=new(grouped,"first","logicalBytes","desc","all");sortDescending=false;SelectTag(SortField,"name");}
            finally{suppressFilters=previous;}
            ChooseBrowserView(details?DetailsMode:ThumbnailMode,new RoutedEventArgs());await RefreshQuery();
            var view=ActiveBrowser;view.SelectedIndex=-1;
            if(continuous)view.SelectRange(new ItemIndexRange(140,320));
            else foreach(int index in new[]{0,300,599})view.SelectRange(new ItemIndexRange(index,1));
            await WaitUntil(()=>!previewLoading&&selected?.Item is not null,TimeSpan.FromSeconds(5));
            string[] expected=await Ids();string? preview=selected!.Item!.EntryId;string prior=resultHandle!.Id;
            int expectedCount=continuous?320:3;
            if(expected.Length!=expectedCount)throw new InvalidOperationException("Native extended selection fixture did not establish its full selection.");
            ((IInvokeProvider)new ButtonAutomationPeer(Descending).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUntil(()=>!queryBusy&&resultHandle?.Id!=prior&&lastAppliedFilter?.Sort.Direction=="desc",TimeSpan.FromSeconds(5));
            await WaitUntil(()=>!previewLoading,TimeSpan.FromSeconds(5));
            string[] actual=await Ids();bool same=expected.SequenceEqual(actual),samePreview=selected?.Item?.EntryId==preview;
            checks.Add(new{details,grouped,continuous,action="direction",before=expected.Length,after=actual.Length,sameIdentities=same,samePreview});
            if(!same||!samePreview)failures.Add($"details={details}, grouped={grouped}, continuous={continuous}");
            prior=resultHandle.Id;SelectTag(SortField,"path");
            await WaitUntil(()=>!queryBusy&&resultHandle?.Id!=prior&&lastAppliedFilter?.Sort.Field=="path",TimeSpan.FromSeconds(5));
            await WaitUntil(()=>!previewLoading,TimeSpan.FromSeconds(5));actual=await Ids();
            same=expected.SequenceEqual(actual);samePreview=selected?.Item?.EntryId==preview;
            checks.Add(new{details,grouped,continuous,action="field",before=expected.Length,after=actual.Length,sameIdentities=same,samePreview});
            if(!same||!samePreview)failures.Add($"field: details={details}, grouped={grouped}, continuous={continuous}");
        }
        report["checks"]=checks;report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException("Sorting lost native selection identities: "+string.Join("; ",failures));
        report["status"]="PASS";
    }
}
