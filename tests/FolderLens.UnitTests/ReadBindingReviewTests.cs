using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ReadBindingReviewTests
{
    [Fact]
    public void HydrationLeasePinsParentAndAncestorsUntilDisposed()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-hydration-pins",Guid.NewGuid().ToString("N")),parent=Path.Combine(root,"parent");Directory.CreateDirectory(parent);
        var info=FileAllocation.InspectMetadata(parent,true);
        using(var lease=new HydrationDirectoryLease(parent,new(info.PhysicalIdentity!,info.ResolvedLocation!)))
        {
            Assert.Throws<IOException>(()=>Directory.Move(parent,parent+".moved"));
            Assert.Throws<IOException>(()=>Directory.Move(root,root+".moved"));
        }
        Directory.Move(root,root+".moved");Assert.True(Directory.Exists(Path.Combine(root+".moved","parent")));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void HydrationNeverStartsForReplacedParent(bool replace)
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-hydration-review",Guid.NewGuid().ToString("N")),parent=Path.Combine(root,"parent");
        Directory.CreateDirectory(parent);string path=Path.Combine(parent,"cloud.jpg");File.WriteAllText(path,"AAAA");File.SetAttributes(path,FileAttributes.Archive|FileAttributes.Offline);
        var info=FileAllocation.InspectMetadata(parent,true);var observed=ScanPathProbe.Read(path,true).FileObservation!;
        var expected=new SourceFileStamp(observed.Bytes,observed.Modified,FileObservationWriter.Signature(observed with{PhysicalIdentity=null,ChangeTime=null}));
        if(replace)
        {
            Directory.Move(parent,parent+".old");Directory.CreateDirectory(parent);File.WriteAllText(path,"BBBB");
            File.SetCreationTimeUtc(path,new DateTime(observed.Created,DateTimeKind.Utc));File.SetLastWriteTimeUtc(path,new DateTime(observed.Modified,DateTimeKind.Utc));File.SetAttributes(path,(FileAttributes)observed.Attributes);
        }
        int hydrated=0;
        try
        {
            Assert.ThrowsAny<Exception>(()=>SourceReadPreparation.Read(path,expected,true,new(info.PhysicalIdentity!,info.ResolvedLocation!),_=>{hydrated++;throw new OperationCanceledException("Counted hydration boundary.");}));
            Assert.Equal(replace?0:1,hydrated);
        }
        finally{File.SetAttributes(path,FileAttributes.Normal);}
    }
    private static async Task<(CatalogStore Store,string Path,string Id,long Version)> Fixture()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-binding-review",Guid.NewGuid().ToString("N")),source=Path.Combine(root,"source");
        Directory.CreateDirectory(source);string path=Path.Combine(source,"sample.jpg");File.WriteAllText(path,"AAAA");
        var store=new CatalogStore(Path.Combine(root,"data"));await store.Initialize();long epoch=await store.OpenRoot("root",source);
        var scanner=new DirectoryIndexer(store);
        scanner.PacketReceived=(_,packet)=>{for(int i=0;i<packet.Entries.Length;i++)packet.Entries[i]=packet.Entries[i] with{PhysicalIdentity=null,ChangeTime=null};};
        await scanner.Scan("root",source,epoch,true,[],null,default);
        var row=Assert.Single((await store.ReadFirstPage(new(){RootId="root"})).Items);
        return(store,path,row.EntryId,row.Version);
    }
    [Fact]
    public async Task UnknownFileIdentityCannotBecomeReusableBinding()
    {
        var f=await Fixture();await using var store=f.Store;await using var probe=new SourceFileProbe();
        store.ReadBindingProbeOverride=(path,allowed,prepare,token)=>
        {
            var packet=ScanPathProbe.Read(path);
            return Task.FromResult(packet.FileObservation is {} item?packet with{PhysicalIdentity=null,FileObservation=item with{PhysicalIdentity=null,ChangeTime=null}}:packet);
        };
        await Assert.ThrowsAsync<IOException>(()=>store.ResolveFileRead("root",f.Id,f.Version,probe,false,default));
        Assert.False((await store.ReadFileProperties("root",f.Id,f.Version))!.ReadObservationBound);
    }
    [Theory]
    [InlineData("epoch",true)][InlineData("location",true)][InlineData("epoch",false)]
    public async Task RebindingMaintainsContentVersionContinuity(string invalidation,bool replace)
    {
        var f=await Fixture();await using var store=f.Store;await using var probe=new SourceFileProbe();
        var a=await store.ResolveFileRead("root",f.Id,f.Version,probe,false,default);
        await store.ApplyFileDetails(f.Id,f.Version,"root",1,new(){State="ready",EncodedWidth=4000,EncodedHeight=2000},"fixture");
        await store.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET display_width=4000,display_height=2000,long_edge=4000,short_edge=2000,pixel_count=8000000 WHERE entry_id=$id; INSERT OR REPLACE INTO FieldStates(entry_id,field_group,source_version,state) VALUES($id,'imageGeometry',$v,'ready')";cmd.Parameters.AddWithValue("$id",f.Id);cmd.Parameters.AddWithValue("$v",f.Version);return cmd.ExecuteNonQuery();});
        if(replace)
        {
            var modified=File.GetLastWriteTimeUtc(f.Path);var created=File.GetCreationTimeUtc(f.Path);
            File.Move(f.Path,f.Path+".old");File.WriteAllText(f.Path,"BBBB");File.SetLastWriteTimeUtc(f.Path,modified);File.SetCreationTimeUtc(f.Path,created);
        }
        if(invalidation=="epoch")await store.OpenRoot("root",Path.GetDirectoryName(f.Path)!);
        else await store.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE DirectoryLocationBindings SET binding_revision=binding_revision+1";return cmd.ExecuteNonQuery();});
        if(!replace)
        {
            var same=await store.ResolveFileRead("root",f.Id,f.Version,probe,false,default);
            Assert.Equal(a.SourceSignature,same.SourceSignature);Assert.Equal(f.Version,same.Version);Assert.Equal(4000,same.Width);Assert.NotNull(same.Details);return;
        }
        await Assert.ThrowsAsync<IOException>(()=>store.ResolveFileRead("root",f.Id,f.Version,probe,false,default));
        Assert.Null(await store.ReadFileProperties("root",f.Id,f.Version));
        var row=Assert.Single((await store.ReadFirstPage(new(){RootId="root"})).Items);
        var b=await store.ResolveFileRead("root",f.Id,row.Version,probe,false,default);
        Assert.True(b.Version>f.Version);Assert.NotEqual(a.SourceSignature,b.SourceSignature);
        Assert.Null(b.Width);Assert.Null(b.Details);Assert.DoesNotContain(b.FieldStates.Values,v=>v.State=="ready");
        Assert.False(await store.ApplyFileDetails(f.Id,f.Version,"root",invalidation=="epoch"?2:1,new(){State="ready",EncodedWidth=4000,EncodedHeight=2000},"late-old-result"));
    }
    [Fact]
    public async Task DeferredSameVolumeDefaultMoveNeedsNoContentRead()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-volume-review",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"deferred.txt");File.WriteAllText(path,"unchanged");File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.Offline);
        try
        {
            Assert.Equal("excluded",ScanPathProbe.Read(path).State);
            Assert.Equal(ShellFileAction.Move,ShellTransferPolicy.Choose([path],directory,false,false));
            Assert.Equal(ShellFileAction.Move,await ShellTransferPolicy.ChooseAsync([path],directory,false,false,null,default));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Offline));
            await using var worker=new ScanWorkerClient(ScanWorkerClient.FindExecutable()!);
            Assert.Equal("excluded",(await worker.Probe(path,default)).State);
            Assert.Equal(FileAllocation.InspectMetadata(directory).VolumeIdentity,(await worker.Probe(path,default,volumeOnly:true)).VolumeIdentity);
            Assert.Equal(ShellFileAction.Copy,await ShellTransferPolicy.ChooseAsync([path+".missing"],directory,false,false,null,default));
            Assert.Equal(ShellFileAction.Copy,await ShellTransferPolicy.ChooseAsync([path],directory,true,false,null,default));
            Assert.Equal(ShellFileAction.Move,await ShellTransferPolicy.ChooseAsync([path],directory,false,true,null,default));
        }
        finally{File.SetAttributes(path,FileAttributes.Normal);}
    }
}
