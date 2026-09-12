using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DirectoryListingTests
{
    [Fact] public void ConcurrentRetirementDoesNotFailSpaceMeasurement()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-listing-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string old=Path.Combine(directory,"old.sqlite"),current=Path.Combine(directory,"new.sqlite");File.WriteAllBytes(old,new byte[20]);File.WriteAllBytes(current,new byte[30]);
        Assert.Equal(30,DirectoryListing.MeasureStorage(directory,path=>{if(path==old)File.Delete(path);}));
        Assert.Equal(30,DirectoryListing.MeasureStorage(directory));
    }
    [Fact] public async Task HundredThousandSiblingsUseStableBoundedPages()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-listing-tests",Guid.NewGuid().ToString("N"));
        await using var listing=DirectoryListing.Create(directory,Enumerable.Range(0,100000).Reverse().Select(i=>$"folder-{i:D6}"),CancellationToken.None);
        Assert.Equal(100000,listing.Count);Assert.Equal(99999,await listing.Find("folder-099999"));Assert.Null(await listing.Find("missing"));
        var first=await listing.ReadPage(0);var last=await listing.ReadPage(99968);
        Assert.Equal(128,first.Count);Assert.Equal(32,last.Count);Assert.Equal("folder-000000",first[0]);Assert.Equal("folder-099999",last[^1]);
        await using var revised=DirectoryListing.Create(directory,["folder-000001","folder-new"],CancellationToken.None);
        Assert.NotEqual(listing.Version,revised.Version);Assert.Equal(first,await listing.ReadPage(0));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>listing.ReadPage(0,cancelled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>listing.ReadPage(-1));
    }
    [Fact] public async Task RecoveryPreservesLiveListingAndDeletesAbandonedOwnedFiles()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-listing-tests",Guid.NewGuid().ToString("N"));
        using var active=DirectoryListing.Create(directory,["live"],CancellationToken.None);
        string orphan=Path.Combine(directory,Guid.NewGuid().ToString("N")+".sqlite"),legacy=Path.Combine(directory,"unowned.sqlite");
        File.WriteAllBytes(orphan,new byte[30]);File.WriteAllText(orphan+".owner","");File.WriteAllText(legacy,"do not infer ownership");
        DirectoryListingLease.Recover(directory);
        Assert.False(File.Exists(orphan));Assert.False(File.Exists(orphan+".owner"));Assert.True(File.Exists(legacy));Assert.Equal(new[]{"live"},await active.ReadPage(0));
    }
    [Fact] public void FailedDeletionCanBeRetriedWithoutLosingOwnership()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-listing-tests",Guid.NewGuid().ToString("N"));
        var listing=DirectoryListing.Create(directory,["live"],CancellationToken.None);string file=Directory.GetFiles(directory,"*.sqlite").Single();
        using(var blocked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))Assert.Equal(32,Assert.Throws<System.ComponentModel.Win32Exception>(listing.Dispose).NativeErrorCode);
        Assert.True(File.Exists(file));listing.Dispose();Assert.False(File.Exists(file));Assert.False(File.Exists(file+".owner"));
    }
    [Fact] public async Task PartialFailureDoesNotPublishOrReplacePreviousListing()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-listing-tests",Guid.NewGuid().ToString("N"));
        await using var previous=DirectoryListing.Create(directory,["A","a"],CancellationToken.None);
        IEnumerable<string> Incomplete(){yield return "new";throw new IOException("offline");}
        Assert.Throws<IOException>(()=>DirectoryListing.Create(directory,Incomplete(),CancellationToken.None));
        Assert.Single(Directory.GetFiles(directory,"*.sqlite"));Assert.Equal(new[]{"A","a"},await previous.ReadPage(0));
    }
}
