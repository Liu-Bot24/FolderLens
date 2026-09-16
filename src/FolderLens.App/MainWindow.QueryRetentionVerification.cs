using FolderLens.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyQueryRetention(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);
        if(metadataTask is not null)await metadataTask;
        var retired=new List<WeakReference<VirtualResults>>();
        var views=new List<WeakReference<object>>();
        var rows=new List<WeakReference<FileRow>>();
        async Task Exercise(bool grouped)
        {
            folderGrouping=new FolderGroupingSpec(Enabled:grouped);
            for(int i=0;i<100;i++)
            {
                var old=results!;object? view=groupedBrowserSource is not null?groupedBrowserSource:flatBrowserItems;
                rows.AddRange(old.CachedRows().Select(row=>new WeakReference<FileRow>(row)));
                suppressFilters=true;Search.Text=i%2==0?"image-0":"";suppressFilters=false;searchTimer?.Stop();
                await RefreshQuery();
                if(!ReferenceEquals(old,results))retired.Add(new(old));
                object? current=groupedBrowserSource is not null?groupedBrowserSource:flatBrowserItems;
                if(view is not null&&!ReferenceEquals(view,current))views.Add(new(view));
                if(results?.Count!=(i%2==0?10:12))throw new InvalidOperationException("Query retention fixture did not publish expected results.");
                await Task.Delay(10);
            }
        }
        await Exercise(false);await Exercise(true);
        await Task.Delay(1000);
        await Task.Run(()=>{GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(2,GCCollectionMode.Forced,true,true);});
        await Task.Delay(500);
        int resultsAlive=retired.Count(reference=>reference.TryGetTarget(out _)),viewsAlive=views.Count(reference=>reference.TryGetTarget(out _));
        report["retiredResults"]=new{observed=retired.Count,alive=resultsAlive};
        report["retiredViews"]=new{observed=views.Count,alive=viewsAlive};
        var currentRows=results!.CachedRows().ToHashSet();
        report["retiredRows"]=new{observed=rows.Count,aliveOutsideCurrentResults=rows.Count(reference=>reference.TryGetTarget(out var row)&&!currentRows.Contains(row))};
        if(resultsAlive>0||viewsAlive>0)throw new InvalidOperationException("Retired query objects remain reachable after replacement and diagnostic collection.");
        report["status"]="PASS";
    }
}
