using System.Diagnostics;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyDirectoryNavigation(string source,Dictionary<string,object> report)
    {
        byte[] png=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));
        for(int i=0;i<3;i++)await File.WriteAllBytesAsync(Path.Combine(source,"B",$"other-{i}.png"),png);
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyCandidateBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
        var switching=OpenRoot(Path.Combine(source,"A"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            report["targetAddressWhileQueryPending"]=RootPath.Text==Path.Combine(source,"A");
            if(!(bool)report["targetAddressWhileQueryPending"])throw new InvalidOperationException("Pending child navigation still displays the scan root address.");
            report["targetTreeWhileQueryPending"]=(FolderTree.SelectedNode?.Content as FolderNode)?.Path==Path.Combine(source,"A");
            if(!(bool)report["targetTreeWhileQueryPending"])throw new InvalidOperationException("Pending child navigation still selects the scan root in the tree.");
        }
        finally{release.TrySetResult();verifyCandidateBarrier=null;await switching;}
        var times=new List<double>();
        for(int i=0;i<12;i++)
        {
            string child=i%2==0?"B":"A";int count=child=="A"?12:3;var watch=Stopwatch.StartNew();
            await OpenRoot(Path.Combine(source,child));
            if(resultHandle?.Count!=count||BrowsedDirectory!=Path.Combine(source,child)||RootPath.Text!=BrowsedDirectory)throw new InvalidOperationException("Directory navigation published another scope's results.");
            var page=await catalog!.ReadPage(resultHandle.Id,0,16,lifetime.Token);
            if(page.Any(item=>!item.RelativePath.StartsWith(child+"\\",StringComparison.Ordinal)))throw new InvalidOperationException("Old directory rows leaked into the new scope.");
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        report["scopeSwitchMilliseconds"]=times;
        if(times.Max()>2000)throw new InvalidOperationException("Local directory navigation took more than two seconds.");
        report["status"]="PASS";
    }
}
