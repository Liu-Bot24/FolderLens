using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CollectionIdentityRegressionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CyclicFileRenamesRemoveOldCollectionsWithoutFollowingFiles(int count)
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(fixture,"source"),data=Path.Combine(fixture,"data");Directory.CreateDirectory(source);
        for(int i=0;i<count;i++)File.WriteAllText(Path.Combine(source,$"{i}.txt"),$"file {i}");
        string[] collections=new string[count];string all;
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();long epoch=await catalog.OpenRoot("root",source);
            var scanner=new DirectoryIndexer(catalog,Worker());await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
            var snapshot=await catalog.CreateSnapshot(new(){RootId="root",Kinds=[]},epoch,1);var rows=await catalog.ReadPage(snapshot.Id,0);
            all=(await catalog.CreateCollection("all")).Id;
            for(int i=0;i<count;i++){collections[i]=(await catalog.CreateCollection($"only-{i}")).Id;await catalog.ChangeCollectionMembers([collections[i],all],[rows.Single(r=>r.RelativePath==$"{i}.txt").EntryId],true);}
            File.Move(Path.Combine(source,"0.txt"),Path.Combine(source,"temp.txt"));
            for(int i=1;i<count;i++)File.Move(Path.Combine(source,$"{i}.txt"),Path.Combine(source,$"{i-1}.txt"));
            File.Move(Path.Combine(source,"temp.txt"),Path.Combine(source,$"{count-1}.txt"));
            await scanner.Scan("root",source,epoch,true,[],null,CancellationToken.None);
        }
        await using(var catalog=new CatalogStore(data))
        {
            await catalog.Initialize();
            var remaining=await catalog.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT f.name,f.relative_path,f.entry_state,f.location_key,m.location_key FROM CollectionMembers m JOIN Files f ON f.entry_id=m.entry_id";using var rows=cmd.ExecuteReader();var lines=new List<string>();while(rows.Read())lines.Add(string.Join(" | ",Enumerable.Range(0,5).Select(rows.GetString)));return string.Join("\n",lines);});
            Assert.True((await catalog.ReadCollections()).Single(c=>c.Id==all).Count==0,remaining);
            for(int i=0;i<count;i++)
            {
                var snapshot=await catalog.CreateSnapshot(new(){RootId="collection:"+collections[i],CollectionId=collections[i],Kinds=[]},1,i+1);
                Assert.Empty(await catalog.ReadPage(snapshot.Id,0));
            }
        }
    }
    [Fact]
    public async Task DifferentParentIdentitiesNeverCollapseMixedCasePaths()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(2);
        await catalog.Write(c=>
        {
            using var tx=c.BeginTransaction();ScanRenames.Ensure(c,tx);using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE Roots SET display_path='C:\fixture' WHERE root_id='benchmark';
                UPDATE Directories SET relative_path='A',case_mode='insensitive' WHERE directory_id='benchmark-dir';
                INSERT INTO Directories(directory_id,root_id,name,relative_path,canonical_key,case_mode) VALUES('lower','benchmark','a','a','a','insensitive');
                INSERT INTO ScanDirectoryIdentities VALUES('benchmark-dir','volume:parent-A'),('lower','volume:parent-a');
                UPDATE Files SET physical_identity='volume:file-A',relative_path='A\photo.jpg' WHERE entry_id='000000000001';
                UPDATE Files SET physical_identity='volume:file-a',directory_id='lower',relative_path='a\photo.jpg' WHERE entry_id='000000000002';
                """;cmd.ExecuteNonQuery();tx.Commit();return true;
        });
        string tag=(await catalog.CreateCollection("both")).Id;
        await catalog.ChangeCollectionMembers([tag],["000000000001"],true);
        Assert.Equal(1,(await catalog.CreateSnapshot(new(){RootId="benchmark",ExcludeCollections=[tag]},1,1)).Count);
        await catalog.ChangeCollectionMembers([tag],["000000000002"],true);
        Assert.Equal(2,(await catalog.ReadCollections()).Single().Count);
        Assert.Equal(2,(await catalog.CreateSnapshot(new(){RootId="collection:"+tag,CollectionId=tag},1,2)).Count);
    }
    private static string Worker()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
        return Path.Combine(root!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
    }
}
