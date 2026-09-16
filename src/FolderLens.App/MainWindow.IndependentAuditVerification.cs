using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyIndependentQuery(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        searchTimer!.Interval=TimeSpan.FromMilliseconds(600);
        Search.Text="image-01";SearchChanged(Search,null!);
        var staleTick=pendingSearchTick??throw new InvalidOperationException("Search was not scheduled.");
        await RefreshQuery();
        if(!await SelectBrowserOrdinal(results!,0,lifetime.Token))throw new InvalidOperationException("Search fixture selection failed.");
        long acceptedGeneration=generation;string? chosen=BrowserPath(selected);
        await staleTick();SearchChanged(Search,null!);
        await Task.Delay(900);
        // Background reconciliation may publish newer metadata in the same query
        // generation. Only a user query may advance generation; neither may lose selection.
        if(generation!=acceptedGeneration||chosen!=BrowserPath(selected))throw new InvalidOperationException("Pending or delayed search replaced an explicitly accepted query or selection.");
        report["explicitQueryConsumesPendingSearch"]=true;
        Search.Text="image-02";
        await WaitUntil(()=>!queryBusy&&lastAppliedFilter?.NamePathQuery=="image-02",TimeSpan.FromSeconds(10));
        if(resultHandle?.Count!=1)throw new InvalidOperationException("New search was incorrectly discarded.");
        report["newSearchStillPublishes"]=true;report["status"]="PASS";
    }
    private async Task VerifyAudioFreshness(string source,Dictionary<string,object> report)
    {
        string path=Path.Combine(source,"freshness.wav");await File.WriteAllTextAsync(path,"invalid WAV");
        suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        await RefreshQuery();
        long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,"freshness.wav")??throw new InvalidOperationException("Missing audio fixture.");
        await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
        int creations=0;verifyAudioSourceCreating=()=>creations++;AudioMute.IsChecked=true;
        try
        {
            var written=File.GetLastWriteTimeUtc(path);File.Move(path,path+".old");
            await File.WriteAllTextAsync(path,"invalid WAV");File.SetLastWriteTimeUtc(path,written);
            await PlayAudioCore();
            if(creations!=0||audio is not null)throw new InvalidOperationException("Replaced audio reached MediaSource creation.");
            report["sameSizeSameTimestampReplacementRejected"]=true;
            await OpenRoot(source,true);await RefreshQuery();
            ordinal=await catalog.FindOrdinal(resultHandle!.Id,"freshness.wav")??throw new InvalidOperationException("Missing refreshed audio.");
            await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
            verifyAudioResolved=()=>{selection++;selected=null;return Task.CompletedTask;};
            await PlayAudioCore();
            if(creations!=0||audio is not null)throw new InvalidOperationException("Retired audio resolution created a source.");
            report["retiredAudioResolutionRejected"]=true;
            report["status"]="PASS";
        }
        finally{verifyAudioSourceCreating=null;verifyAudioResolved=null;StopAudio();}
    }
}
