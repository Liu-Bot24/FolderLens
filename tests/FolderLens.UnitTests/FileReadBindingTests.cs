using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class FileReadBindingTests
{
    [Fact]
    public async Task DirectoryWithoutFileIdsBindsFullObservationBeforeDecode()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-read-binding",Guid.NewGuid().ToString("N")),source=Path.Combine(root,"source");
        Directory.CreateDirectory(source);string path=Path.Combine(source,"first.jpg");File.WriteAllText(path,"stable source");
        await using var catalog=new CatalogStore(Path.Combine(root,"index"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source);var scanner=new DirectoryIndexer(catalog);
        // Same metadata shape as FileFullDirectoryInfo (class 14): file IDs
        // absent in enumeration, but available on a single-file handle.
        scanner.PacketReceived=(_,packet)=>{for(int i=0;i<packet.Entries.Length;i++)packet.Entries[i]=packet.Entries[i] with{PhysicalIdentity=null};};
        await scanner.Scan("root",source,epoch,true,[],null,default);
        var row=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        var before=(await catalog.ReadFileProperties("root",row.EntryId,row.Version))!;
        var actual=FileReadObservation.Read(path);
        var nativePartial=Assert.Single(ScanDirectoryReader.Read(source,false,false,true).SelectMany(packet=>packet.Entries));
        Assert.Null(nativePartial.PhysicalIdentity);Assert.Equal(before.SourceSignature,FileObservationWriter.Signature(nativePartial));
        Assert.Throws<IOException>(()=>new ApprovedInput(path,before.LogicalBytes,before.ModifiedUtcTicks,SourceSignature:before.SourceSignature).Observe(actual));
        await using var probe=new SourceFileProbe();
        var ready=await catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,false,default);
        Assert.Equal(actual.Signature,ready.SourceSignature);
        new ApprovedInput(path,ready.LogicalBytes,ready.ModifiedUtcTicks,SourceSignature:ready.SourceSignature).Observe(actual);
        Assert.Equal(row.Version,ready.Version);
        Assert.True(ready.ReadObservationBound);
        catalog.ReadBindingProbeOverride=(_,_,_,_)=>throw new InvalidOperationException("A bound observation was probed again.");
        Assert.Equal(ready.SourceSignature,(await catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,false,default)).SourceSignature);
        await scanner.Scan("root",source,epoch,true,[],null,default);
        var rescanned=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        Assert.Equal(row.Version,rescanned.Version);
        Assert.Equal(ready.SourceSignature,(await catalog.ReadFileProperties("root",row.EntryId,row.Version))!.SourceSignature);
        var modified=File.GetLastWriteTimeUtc(path);var created=File.GetCreationTimeUtc(path);
        File.Move(path,path+".old");File.WriteAllText(path,"changed bytes");File.SetLastWriteTimeUtc(path,modified);File.SetCreationTimeUtc(path,created);
        var retained=await catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,false,default);
        Assert.Equal(ready.SourceSignature,retained.SourceSignature);
        Assert.Throws<IOException>(()=>new ApprovedInput(path,retained.LogicalBytes,retained.ModifiedUtcTicks,SourceSignature:retained.SourceSignature).Observe(FileReadObservation.Read(path)));
    }
    [Fact]
    public async Task CloudBindingRequiresApprovalAndUsesPostHydrationObservation()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-cloud-binding",Guid.NewGuid().ToString("N")),source=Path.Combine(root,"source");
        Directory.CreateDirectory(source);string path=Path.Combine(source,"cloud.jpg");File.WriteAllText(path,"source");
        await using var catalog=new CatalogStore(Path.Combine(root,"index"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source);var scanner=new DirectoryIndexer(catalog);
        scanner.PacketReceived=(_,packet)=>{for(int i=0;i<packet.Entries.Length;i++)packet.Entries[i]=packet.Entries[i] with{PhysicalIdentity=null,ChangeTime=null,Hydration="placeholder",Attributes=packet.Entries[i].Attributes|0x1000};};
        await scanner.Scan("root",source,epoch,true,[],null,default);
        var row=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        int probes=0,preparations=0;
        catalog.ReadBindingProbeOverride=(target,allowed,prepare,token)=>
        {
            Assert.True(allowed);probes++;
            if(prepare is not null){Assert.True(FileAllocation.IsDeferred(SourceObservationSignature.Parse(prepare.Value.SourceSignature!).Attributes));preparations++;}
            return Task.FromResult(ScanPathProbe.Read(target));
        };
        await using var probe=new SourceFileProbe();
        var deferred=await catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,false,default);
        Assert.Equal("placeholder",deferred.HydrationState);Assert.Equal(0,probes);Assert.False(deferred.ReadObservationBound);
        var ready=await catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,true,default);
        Assert.Equal(1,preparations);Assert.Equal(4,probes);Assert.Equal("local",ready.HydrationState);
        Assert.Equal(FileReadObservation.Read(path).Signature,ready.SourceSignature);
        Assert.Equal(row.Version,ready.Version);
    }
    [Theory]
    [InlineData("file")][InlineData("parent")][InlineData("epoch")][InlineData("cancel")]
    public async Task ChangedObservationCannotBind(string fault)
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-read-binding",Guid.NewGuid().ToString("N")),source=Path.Combine(root,"source");
        Directory.CreateDirectory(source);string path=Path.Combine(source,"first.jpg");File.WriteAllText(path,"source");
        await using var catalog=new CatalogStore(Path.Combine(root,"index"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source);var scanner=new DirectoryIndexer(catalog);
        scanner.PacketReceived=(_,packet)=>{for(int i=0;i<packet.Entries.Length;i++)packet.Entries[i]=packet.Entries[i] with{PhysicalIdentity=null};};
        await scanner.Scan("root",source,epoch,true,[],null,default);
        var row=Assert.Single((await catalog.ReadFirstPage(new(){RootId="root"})).Items);
        var before=(await catalog.ReadFileProperties("root",row.EntryId,row.Version))!;
        using var stop=new CancellationTokenSource();
        catalog.ReadBindingProbeOverride=async(target,allowed,prepare,token)=>
        {
            var observed=prepare is null?ScanPathProbe.Read(target,allowed):SourceReadPreparation.Read(target,prepare.Value,allowed);
            if(prepare is not null)
            {
                if(fault=="file")
                {
                    var info=new FileInfo(path);long modified=info.LastWriteTimeUtc.Ticks,created=info.CreationTimeUtc.Ticks;
                    File.Move(path,path+".old");File.WriteAllText(path,"change");
                    File.SetLastWriteTimeUtc(path,new DateTime(modified,DateTimeKind.Utc));File.SetCreationTimeUtc(path,new DateTime(created,DateTimeKind.Utc));
                }
                if(fault=="parent"){Directory.Move(source,source+".old");Directory.CreateDirectory(source);File.Copy(Path.Combine(source+".old","first.jpg"),path);}
                if(fault=="epoch")await catalog.OpenRoot("root",source);
                if(fault=="cancel")stop.Cancel();
            }
            return observed;
        };
        await using var probe=new SourceFileProbe();
        if(fault=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,false,stop.Token));
        else await Assert.ThrowsAsync<IOException>(()=>catalog.ResolveFileRead("root",row.EntryId,row.Version,probe,false,stop.Token));
        Assert.False((await catalog.ReadFileProperties("root",row.EntryId,row.Version))!.ReadObservationBound);
    }
    [Fact]
    public void CompletingObservationPreservesEveryKnownFieldAndCloudConsent()
    {
        var partial=SourceObservationSignature.Parse("10:20:30:::32");
        var full=SourceObservationSignature.Parse("10:20:30:40:legacy:AB:01:30:32");
        Assert.True(partial.CanCompleteWith(full));
        foreach(var mismatch in new[]{full with{Bytes=11},full with{Modified=21},full with{Created=31},full with{Attributes=0}})
            Assert.False(partial.CanCompleteWith(mismatch));
        Assert.False(full.CanCompleteWith(full with{Identity="different"}));
        Assert.False(full.CanCompleteWith(full with{Change=41}));
        var cloud=full with{Attributes=full.Attributes|0x1000|0x400000};
        Assert.True(cloud.SameFileAfterHydration(full with{Change=41}));
        Assert.False(cloud.SameFileAfterHydration(full with{Identity="different"}));
        Assert.False(cloud.SameFileAfterHydration(full with{Modified=21}));
        Assert.False(cloud.SameFileAfterHydration(full with{Attributes=1}));
        Assert.False((cloud with{Identity=""}).SameFileAfterHydration(full));
    }
    [Fact]
    public async Task NormalVolumeChoiceKeepsCopyMoveAndModifierSemantics()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-transfer-volume",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string path=Path.Combine(root,"item.txt");File.WriteAllText(path,"file");
        Assert.Equal(ShellTransferPolicy.Choose([path],root,false,false),await ShellTransferPolicy.ChooseAsync([path],root,false,false,null,default));
        Assert.Equal(ShellFileAction.Copy,await ShellTransferPolicy.ChooseAsync([path],root,true,false,null,default));
        Assert.Equal(ShellFileAction.Move,await ShellTransferPolicy.ChooseAsync([path],root,false,true,null,default));
        await Assert.ThrowsAsync<NotSupportedException>(()=>ShellTransferPolicy.ChooseAsync([path],root,true,true,null,default));
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ShellTransferPolicy.ChooseAsync([path],root,false,false,null,stop.Token));
    }
}
