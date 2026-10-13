using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyDriveLabels(Dictionary<string,object> report)
    {
        var computer=computerTreeRoot??throw new InvalidOperationException("Missing computer root.");
        var nodes=computer.Children.ToArray();
        if(nodes.Length==0)throw new InvalidOperationException("Missing drive roots.");
        var first=nodes[0];var original=(FolderNode)first.Content;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Read(string path,CancellationToken token)
        {
            if(path==original.Path){entered.TrySetResult();await release.Task.WaitAsync(token);}
            return NavigationDriveNames.Format(path,$@"Photos (旧) (\\server) ({path[..2]})");
        }
        var loading=RefreshDriveLabels(Read);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(nodes.Length>1)await WaitUntil(()=>nodes.Skip(1).All(node=>((FolderNode)node.Content).Label.Contains("Photos")),TimeSpan.FromSeconds(3));
            if(((FolderNode)first.Content).Label!=original.Label)throw new InvalidOperationException("Blocked name lookup changed its node prematurely.");
            first.Content=original with{CatalogRoot="fixture-catalog",BasePath=original.Path};
            release.TrySetResult();await loading.WaitAsync(TimeSpan.FromSeconds(3));
            var updated=(FolderNode)first.Content;
            if(updated.CatalogRoot!="fixture-catalog"||updated.BasePath!=original.Path||updated.Label!=original.Path[..2]+" Photos (旧)"||!nodes.SequenceEqual(computer.Children))
                throw new InvalidOperationException("Drive label changed navigation identity, current metadata or root nodes.");
            string empty=Path.Combine(dataDirectory,"drive-label-empty");Directory.CreateDirectory(empty);
            await using(var listing=await FolderChildren.Read(empty,TreeListingDirectory,lifetime.Token))
            {
                var earlier=new TreeListing(listing,0,original with{CatalogRoot=null});
                ApplyTreePage(first,earlier,[]);ApplyTreePage(first,earlier,[]);
                if(first.Content is not FolderNode retained||retained.Label!=updated.Label||retained.CatalogRoot is not null)
                    throw new InvalidOperationException("A late or cached directory page reverted the new drive name or retained stale routing.");
            }
            IEnumerable<TextBlock> Labels(DependencyObject parent)
            {
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                {
                    var child=VisualTreeHelper.GetChild(parent,i);
                    if(child is TextBlock text)yield return text;
                    foreach(var nested in Labels(child))yield return nested;
                }
            }
            FolderTree.UpdateLayout();
            await WaitUntil(()=>Labels(FolderTree).Any(text=>text.Text==updated.Label),TimeSpan.FromSeconds(3));
            report["renderedDriveLabel"]=updated.Label;
            report["slowDriveDoesNotBlockOtherNames"]=true;
            report["nodeIdentityAndNavigationMetadataPreserved"]=true;
            report["lateAndCachedDirectoryPagesRetainName"]=true;
            report["namesAreGeneratedFixtures"]=true;
            report["status"]="PASS";
        }
        finally{release.TrySetResult();await loading;}
    }
}
