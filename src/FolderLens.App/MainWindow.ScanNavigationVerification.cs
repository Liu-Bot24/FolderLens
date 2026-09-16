using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyScanNavigationScale(string source,Dictionary<string,object> report)
    {
        // A real worker must be mid-enumeration: no completion barrier here.
        await Task.Run(()=>
        {
            for(int folder=0;folder<1024;folder++)
            {
                string directory=Path.Combine(source,"bulk",$"folder-{folder:D4}");Directory.CreateDirectory(directory);
                for(int file=0;file<64;file++)File.WriteAllBytes(Path.Combine(directory,$"item-{file:D2}.dat"),[]);
            }
        });
        Task parent=OpenRoot(source);
        var wait=System.Diagnostics.Stopwatch.StartNew();
        while(!scanProgress.Values.Any(p=>p.Progress.Files>=64))
        {
            if(parent.IsCompleted||wait.Elapsed>TimeSpan.FromSeconds(60))throw new InvalidOperationException("Large parent scan did not reach a cancellable in-flight state.");
            await Task.Delay(20);
        }
        var retired=activeBackgroundScan??throw new InvalidOperationException("Large parent scan finished before navigation.");
        if(retired.Completion.IsCompleted)throw new InvalidOperationException("Large scan fixture failed to exercise active cancellation.");
        report["parentFilesAtNavigation"]=scanProgress.Values.Max(p=>p.Progress.Files);
        var navigation=System.Diagnostics.Stopwatch.StartNew();
        await OpenRoot(Path.Combine(source,"A")).WaitAsync(TimeSpan.FromSeconds(20));
        await parent.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        if(!retired.Cancelled||!retired.Completion.IsCompleted||root!=Path.Combine(source,"A")||resultHandle?.Count!=12||
            scanProgress.Values.Any(p=>p.Progress.Files!=12)||backgroundScans.Any(s=>s.Path!=root&&!s.Completion.IsCompleted))
            throw new InvalidOperationException("Large parent scan survived navigation or contaminated the selected child.");
        report["generatedParentFiles"]=65548;report["selectedChildFiles"]=12;
        report["navigationMs"]=navigation.Elapsed.TotalMilliseconds;report["oldWorkerCompleted"]=true;report["status"]="PASS";
    }
    private async Task VerifyScanNavigation(string source,Dictionary<string,object> report)
    {
        byte[] png=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=0;i<3;i++)await File.WriteAllBytesAsync(Path.Combine(source,"B",$"second-{i}.png"),png);
        string third=Path.Combine(source,"C");Directory.CreateDirectory(third);
        await File.WriteAllBytesAsync(Path.Combine(third,"third.png"),png);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled=false;
        verifyBackgroundScanCompletionBarrier=async token=>
        {
            entered.TrySetResult();
            try{await release.Task.WaitAsync(token);}
            finally{cancelled=token.IsCancellationRequested;}
        };
        Task original=OpenRoot(source);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var retired=activeBackgroundScan??throw new InvalidOperationException("Expected an active parent scan.");
            verifyBackgroundScanCompletionBarrier=null;
            await OpenRoot(Path.Combine(source,"A")).WaitAsync(TimeSpan.FromSeconds(10));
            if(!cancelled||!retired.Completion.IsCompleted||root!=Path.Combine(source,"A")||CurrentFilter().DirectoryScope!=""||resultHandle?.Count!=12)
                throw new InvalidOperationException("Child navigation retained the parent scan or its result scope.");
            if(scanProgress.Values.Any(p=>p.Progress.Files!=12)||backgroundScans.Any(s=>s.Path!=root&&!s.Completion.IsCompleted))
                throw new InvalidOperationException("Previous root work or counters survived navigation.");
            report["parentScanCancelledBeforeChildPublished"]=true;
            Task first=OpenRoot(Path.Combine(source,"B")),last=OpenRoot(third);
            await Task.WhenAll(first,last).WaitAsync(TimeSpan.FromSeconds(15));
            if(root!=third||RootPath.Text!=third||resultHandle?.Count!=1||backgroundScans.Any(s=>s.Path!=third&&!s.Completion.IsCompleted))
                throw new InvalidOperationException("Rapid navigation published or scanned an obsolete directory.");
            report["rapidNavigationKeepsLatestOnly"]=true;
            await OpenRoot(source).WaitAsync(TimeSpan.FromSeconds(15));
            if(root!=source||resultHandle?.Count!=16)throw new InvalidOperationException("Parent navigation did not enumerate the selected parent subtree.");
            await OpenRoot(Path.Combine(source,"B")).WaitAsync(TimeSpan.FromSeconds(10));
            if(root!=Path.Combine(source,"B")||resultHandle?.Count!=3||scanProgress.Values.Any(p=>p.Progress.Files!=3))
                throw new InvalidOperationException("Navigation after a completed scan inherited parent counters.");
            report["parentAndCompletedRootNavigation"]=true;
            var legacy=CaptureView() with{Root=source,Filter=CurrentFilter() with{DirectoryScope="A"},SelectedPath=Path.Combine("A","image-00.png"),ScrollAnchorPath=Path.Combine("A","image-00.png")};
            await RestoreSavedView(legacy);
            if(root!=Path.Combine(source,"A")||CurrentFilter().DirectoryScope!=""||selected?.RelativePath!="image-00.png")
                throw new InvalidOperationException("Saved child scope resumed the parent scan or lost its rebased selection.");
            report["legacyChildScopeRestoresSelectedRoot"]=true;report["status"]="PASS";
        }
        finally{verifyBackgroundScanCompletionBarrier=null;release.TrySetResult();await original;}
    }
}
