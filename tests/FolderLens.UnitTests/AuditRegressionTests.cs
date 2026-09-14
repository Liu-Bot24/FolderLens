using System.Diagnostics;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class AuditRegressionTests
{
    [Theory]
    [InlineData(false,false,false,false,false)]
    [InlineData(true,false,false,false,false)]
    [InlineData(true,true,false,false,true)]
    [InlineData(true,false,true,false,true)]
    [InlineData(true,false,false,true,true)]
    public void ListSelectionStillAllowsNewScanResults(bool selected,bool immersive,bool fullScreen,bool slideshow,bool locked)
    {
        var policy=new ScanPreviewRefresh();
        Assert.Equal(!locked,policy.TryBegin(200,true,false,ScanPreviewRefresh.SequenceLocked(true,selected,immersive,fullScreen,slideshow),TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RegisterAndUnregisterDoNotRunProcessSamplingOnCaller()
    {
        var resources=new WorkerResources(new(123,16L<<30,6L<<30,8L<<30,0,0,0,16,false,true));
        using var process=Process.GetCurrentProcess();
        resources.Register(process);
        try{Assert.Equal(123,resources.Snapshot.ProcessTreeBytes);}
        finally{resources.Unregister(process.Id);}
        Assert.Equal(123,resources.Snapshot.ProcessTreeBytes);
    }

    [Fact]
    public async Task SmallCatalogBudgetStopsPartiallyAndKeepsCommittedFilesReadable()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(directory,"source");Directory.CreateDirectory(source);
        for(int i=0;i<1200;i++)File.WriteAllText(Path.Combine(source,new string('x',140)+$"{i:D4}.jpg"),"");
        await using var catalog=new CatalogStore(Path.Combine(directory,"db"));await catalog.Initialize();
        await catalog.Write(c=>{CompactBrowsingCatalog.Configure(c,2L<<20);return true;});
        long epoch=await catalog.OpenRoot("r",source);
        var result=await new DirectoryIndexer(catalog).Scan("r",source,epoch,true,[],null,CancellationToken.None);
        Assert.Equal("partial",result.State);
        Assert.True(result.BudgetLimited);Assert.True(catalog.BrowsingBudgetReached);
        await Assert.ThrowsAsync<BrowsingBudgetException>(()=>catalog.EnsureBrowsingBudget(CancellationToken.None));
        Assert.InRange(result.Files,1,1199);
        var first=await catalog.ReadFirstPage(new FilterSpec{RootId="r",Kinds=["image"]});
        Assert.NotEmpty(first.Items);
        var state=await catalog.Read(c=>{using var q=c.CreateCommand();q.CommandText="SELECT scan_state FROM Roots WHERE root_id='r'";return (string)q.ExecuteScalar()!;});
        Assert.Equal("partial",state);
    }
}
