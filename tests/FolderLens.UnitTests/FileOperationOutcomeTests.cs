using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class FileOperationOutcomeTests
{
    [Theory]
    [InlineData("photo.JPG","photo.jpg")]
    [InlineData("a.txt","A.txt")]
    public async Task CaseOnlyRenameChangesStoredName(string original,string renamed)
    {
        string folder=Temp();Directory.CreateDirectory(folder);
        string source=Path.Combine(folder,original),destination=Path.Combine(folder,renamed);
        await File.WriteAllTextAsync(source,"generated case test");
        var result=await ShellFileOperations.ExecuteCore([new(source,ShellFileAction.Rename,NewName:renamed)],0,CancellationToken.None,true);
        Assert.True(Assert.Single(result.Items).Outcome==ShellItemOutcome.Completed,System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(renamed,Path.GetFileName(Assert.Single(Directory.EnumerateFiles(folder))));
        Assert.Equal("generated case test",await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task CopyWithRetainedSourceMustNotBeClassifiedAsCompletedMove()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"source.txt");
        string destinationFolder=Environment.GetEnvironmentVariable("FOLDERLENS_TEST_OTHER_VOLUME")??Temp();
        destinationFolder=Path.Combine(destinationFolder,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(destinationFolder);
        string destination=Path.Combine(destinationFolder,"target.txt");
        await File.WriteAllTextAsync(source,"generated cross-volume test");
        if(Environment.GetEnvironmentVariable("FOLDERLENS_TEST_OTHER_VOLUME") is not null)Assert.NotEqual(FileAllocation.InspectMetadata(folder).VolumeIdentity,FileAllocation.InspectMetadata(destinationFolder).VolumeIdentity);
        var request=Request(source,destination,FileOperationKind.Move);
        var copied=await ShellFileOperations.ExecuteCore([new(source,ShellFileAction.Copy,destinationFolder)],0,CancellationToken.None,true);
        destination=Assert.Single(copied.Items).ActualDestination!;
        Assert.Equal("generated cross-volume test",await File.ReadAllTextAsync(destination));
        Assert.Equal(request.PhysicalIdentity,FileAllocation.InspectMetadata(source).PhysicalIdentity);
        Assert.Equal(ShellItemOutcome.SourceRetained,ShellFileOperations.VerifyOutcome(new(source,ShellFileAction.Move,destinationFolder),destination,0,FileAllocation.InspectMetadata(folder).PhysicalIdentity));
    }

    private static FileOperationRequest Request(string source,string destination,FileOperationKind kind)
    {var info=new FileInfo(source);return new(source,new SourceFileStamp(info.Length,info.LastWriteTimeUtc.Ticks),kind,destination,FileAllocation.InspectMetadata(source).PhysicalIdentity);}
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-operation-outcomes",Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShellBatchMoveOrCopyUsesRealFilesystemAndReleasesHandles(bool move)
    {
        string sourceFolder=Temp(),destination=Path.Combine(Environment.GetEnvironmentVariable("FOLDERLENS_TEST_OTHER_VOLUME")??Temp(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(sourceFolder);Directory.CreateDirectory(destination);
        var requests=new List<ShellFileRequest>();
        for(int i=0;i<3;i++){string path=Path.Combine(sourceFolder,$"生成文件 {i}.txt");await File.WriteAllTextAsync(path,$"payload {i}");requests.Add(new(path,move?ShellFileAction.Move:ShellFileAction.Copy,destination));}
        var result=await ShellFileOperations.ExecuteCore(requests,0,CancellationToken.None,true);
        Assert.False(result.Aborted);Assert.All(result.Items,item=>Assert.Equal(ShellItemOutcome.Completed,item.Outcome));
        for(int i=0;i<3;i++)
        {
            Assert.Equal(!move,File.Exists(requests[i].Source));string target=Path.Combine(destination,Path.GetFileName(requests[i].Source));Assert.Equal($"payload {i}",await File.ReadAllTextAsync(target));
            using var exclusive=File.Open(target,FileMode.Open,FileAccess.Read,FileShare.None);
        }
    }
    [Fact]
    public async Task FailedOrCanceledShellOperationsKeepOriginalAndExistingTarget()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"a.txt"),target=Path.Combine(folder,"b.txt");await File.WriteAllTextAsync(source,"first");await File.WriteAllTextAsync(target,"second");
        await Assert.ThrowsAsync<IOException>(()=>ShellFileOperations.ExecuteCore([new(source,ShellFileAction.Rename,NewName:"b.txt")],0,CancellationToken.None,true));
        using var stop=new CancellationTokenSource();stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ShellFileOperations.Execute([new(source,ShellFileAction.Rename,NewName:"c.txt")],0,stop.Token));
        Assert.Equal("first",await File.ReadAllTextAsync(source));Assert.Equal("second",await File.ReadAllTextAsync(target));
        Assert.Equal(ShellItemOutcome.NotCompleted,ShellFileOperations.VerifyOutcome(new(source,ShellFileAction.Move,folder),target,0x00270005,FileAllocation.InspectMetadata(folder).PhysicalIdentity));
        Assert.Equal(ShellItemOutcome.Indeterminate,ShellFileOperations.VerifyOutcome(new(source,ShellFileAction.Move,folder),Path.Combine(folder,"absent.txt"),0,null));
    }
    [Fact]
    public async Task DefaultDragAndModifierRulesUseVolumeIdentity()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"a.txt");await File.WriteAllTextAsync(source,"generated");
        Assert.Equal(ShellFileAction.Move,ShellTransferPolicy.Choose([source],folder,false,false));
        Assert.Equal(ShellFileAction.Copy,ShellTransferPolicy.Choose([source],folder,true,false));
        string other=Environment.GetEnvironmentVariable("FOLDERLENS_TEST_OTHER_VOLUME")??folder;Directory.CreateDirectory(other);
        Assert.Equal(ShellFileAction.Move,ShellTransferPolicy.Choose([source],other,false,true));
        if(other!=folder)Assert.Equal(ShellFileAction.Copy,ShellTransferPolicy.Choose([source],other,false,false));
        Assert.Throws<NotSupportedException>(()=>ShellTransferPolicy.Choose([source],folder,true,true));
    }
    [Theory]
    [InlineData("renamed.txt")]
    [InlineData("FIRST.txt")]
    public async Task AddingOrCopyingFilesDoesNotChangePlaylistAndExternalRenameInvalidatesOnlyItsLink(string newName)
    {
        string folder=Temp(),source=Path.Combine(folder,"source"),playlist=Path.Combine(folder,"saved.sqlite");Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source,"first.txt"),"one");await File.WriteAllTextAsync(Path.Combine(source,"second.txt"),"two");
        await using(var catalog=new CatalogStore(Path.Combine(folder,"runtime"),new(),playlist))
        {
            await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
            var items=(await catalog.ReadFirstPage(new(){RootId="root",Kinds=[]})).Items;
            var collection=await catalog.CreateCollection("generated");await catalog.ChangeCollectionItems([collection.Id],items,true);
            await File.WriteAllTextAsync(Path.Combine(source,"new.txt"),"new");File.Copy(Path.Combine(source,"first.txt"),Path.Combine(source,"copy.txt"));
            await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);await catalog.RefreshPlaylist(collection.Id);
            Assert.Equal(2L,CountSaved(playlist));
            var result=await ShellFileOperations.ExecuteCore([new(Path.Combine(source,"first.txt"),ShellFileAction.Rename,NewName:newName)],0,CancellationToken.None,true);
            Assert.Equal(ShellItemOutcome.Completed,Assert.Single(result.Items).Outcome);
            // No operation-specific playlist cleanup; the same validator handles Explorer changes.
            await catalog.RefreshPlaylist(collection.Id);Assert.Equal(1L,CountSaved(playlist));
        }
        await using var restarted=new CatalogStore(Path.Combine(folder,"next-runtime"),new(),playlist);await restarted.Initialize();Assert.Equal(1L,CountSaved(playlist));
    }
    private static long CountSaved(string path){using var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText="SELECT count(*) FROM SavedLinks";return (long)q.ExecuteScalar()!;}
    [Fact]
    public async Task CopyIntoSameDirectoryLetsShellCreateDistinctName()
    {
        string folder=Temp();Directory.CreateDirectory(folder);string source=Path.Combine(folder,"sample.txt");await File.WriteAllTextAsync(source,"generated");
        var result=await ShellFileOperations.ExecuteCore([new(source,ShellFileAction.Copy,folder)],0,CancellationToken.None,true);
        var item=Assert.Single(result.Items);Assert.Equal(ShellItemOutcome.Completed,item.Outcome);Assert.NotEqual(source,item.ActualDestination);
        Assert.Equal(2,Directory.GetFiles(folder).Length);Assert.Equal("generated",await File.ReadAllTextAsync(source));
    }
}
