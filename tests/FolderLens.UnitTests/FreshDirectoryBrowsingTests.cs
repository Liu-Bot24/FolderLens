using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class FreshDirectoryBrowsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshOfRemovedLocalRootRetiresFilesWhenParentStillExists(bool isolated)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string root=Path.Combine(fixture,"source");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"old.jpg"),"image");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",root);var scanner=new DirectoryIndexer(catalog,isolated?Worker():null);
        await scanner.Scan("root",root,epoch,true,[],null,CancellationToken.None);
        Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        Directory.Move(root,Path.Combine(fixture,"removed"));
        var after=await scanner.Scan("root",root,epoch,true,[],null,CancellationToken.None,true);
        Assert.Empty((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        Assert.Equal(0,(await catalog.CreateSnapshot(new(){RootId="root",Grouping=new(true,"first")},epoch,2)).Count);
        Assert.Equal("missing",after.State);
    }

    [Fact]
    public async Task ReopeningShowsOnlyNewlyObservedFilesAndFormatsIncludingGroupedResults()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string root=Path.Combine(fixture,"source");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"old.jpg"),"old");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();
        long oldEpoch=await catalog.OpenRoot("root",root);var scanner=new DirectoryIndexer(catalog);
        await scanner.Scan("root",root,oldEpoch,true,[],null,CancellationToken.None);
        File.Move(Path.Combine(root,"old.jpg"),Path.Combine(fixture,"old.jpg"));File.WriteAllText(Path.Combine(root,"new.png"),"new");
        long epoch=await catalog.OpenRoot("root",root);
        var filter=new FilterSpec{RootId="root",ObservedRootEpoch=epoch};
        Assert.Empty((await catalog.ReadFirstPage(filter)).Items);
        Assert.Empty((await catalog.ReadFileExtensions(filter)).Values);
        Assert.Equal(0,(await catalog.CreateSnapshot(filter with{Grouping=new(true,"first")},epoch,1)).Count);
        await scanner.Scan("root",root,epoch,true,[],null,CancellationToken.None);
        Assert.Equal("new.png",Assert.Single((await catalog.ReadFirstPage(filter)).Items).RelativePath);
        Assert.Equal("png",Assert.Single((await catalog.ReadFileExtensions(filter)).Values));
        Assert.Equal(1,(await catalog.CreateSnapshot(filter with{Grouping=new(true,"first")},epoch,2)).Count);
    }

    [Fact]
    public async Task MissingPathOnDifferentVolumeCannotRetireOldEntries()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string root=Path.Combine(fixture,"source");Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"old.jpg"),"image");
        await using var catalog=new CatalogStore(Path.Combine(fixture,"data"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",root);var scanner=new DirectoryIndexer(catalog);
        await scanner.Scan("root",root,epoch,true,[],null,CancellationToken.None);
        Directory.Move(root,Path.Combine(fixture,"removed"));
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Roots SET volume_identity='DIFFERENT:ROOT:IDENTITY' WHERE root_id='root'";return cmd.ExecuteNonQuery();});
        await Assert.ThrowsAsync<RootIdentityChangedException>(()=>scanner.Scan("root",root,epoch,true,[],null,CancellationToken.None));
        Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        long reopened=await catalog.OpenRoot("root",root);
        Assert.Empty((await catalog.ReadFirstPage(new(){RootId="root",ObservedRootEpoch=reopened})).Items);
    }
    private static string Worker()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"Directory.Build.props")))project=project.Parent;
        return Path.Combine(project!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
    }
}
