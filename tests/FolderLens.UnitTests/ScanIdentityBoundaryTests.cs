using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScanIdentityBoundaryTests
{
    [Fact]
    public async Task IndexerRejectsCompletionIfDirectoryChangesAfterWorkerReply()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-scan-boundary",Guid.NewGuid().ToString("N")),source=Path.Combine(root,"source");
        Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"keep.jpg"),"a");File.WriteAllText(Path.Combine(source,"unseen.jpg"),"b");
        await using var catalog=new CatalogStore(Path.Combine(root,"index"));await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source);var scanner=new DirectoryIndexer(catalog);
        await scanner.Scan("root",source,epoch,true,[],null,default);
        File.Move(Path.Combine(source,"unseen.jpg"),Path.Combine(root,"unseen.jpg"));bool replaced=false;
        scanner.PacketReceived=(path,packet)=>
        {
            if(path==source&&packet.State=="completed"&&!replaced)
            {replaced=true;Directory.Move(source,source+"-old");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"new.jpg"),"new");}
        };
        var outcome=await scanner.Scan("root",source,epoch,true,[],null,default);
        Assert.True(replaced);Assert.Equal("partial",outcome.State);
        var page=await catalog.ReadFirstPage(new(){RootId="root"});
        Assert.Contains(page.Items,item=>item.RelativePath=="unseen.jpg");
        Assert.DoesNotContain(page.Items,item=>item.RelativePath=="new.jpg");
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ReplacedDirectoryCannotPublishNewEntriesUnderOldIdentity(bool afterBatch)
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-scan-identity",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        for(int i=0;i<300;i++)File.WriteAllText(Path.Combine(source,$"old-{i:D3}.txt"),"old");
        using var cursor=ScanDirectoryReader.Read(source).GetEnumerator();
        Assert.True(cursor.MoveNext());Assert.Equal("started",cursor.Current.State);
        if(afterBatch){Assert.True(cursor.MoveNext());Assert.Equal("batch",cursor.Current.State);}
        Directory.Move(source,Path.Combine(root,"retired"));Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source,"new.txt"),"new");
        var packets=new List<ScanDirectoryPacket>();
        try{while(cursor.MoveNext())packets.Add(cursor.Current);}catch(IOException){}
        Assert.DoesNotContain(packets,p=>p.State=="completed");
        Assert.DoesNotContain(packets.SelectMany(p=>p.Entries),entry=>entry.Name=="new.txt");
    }
    [Fact]
    public void StableDirectoryReturnsMatchingFileIdentitiesAndCompletes()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-scan-identity",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string file=Path.Combine(root,"valid.txt");File.WriteAllText(file,"abc");
        var packets=ScanDirectoryReader.Read(root).ToArray();var entry=Assert.Single(packets.SelectMany(p=>p.Entries));
        Assert.Equal(FileAllocation.InspectMetadata(file).PhysicalIdentity,entry.PhysicalIdentity);
        Assert.Equal(3,entry.Bytes);Assert.Equal("completed",packets[^1].State);
    }
}
