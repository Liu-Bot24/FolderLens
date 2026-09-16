using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ScopedDirectorySnapshotTests
{
    [Fact]
    public async Task TinyScopeCopiesOnlySubtreeAndAncestorsAndFreezesNames()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-scoped-dirs",Guid.NewGuid().ToString("N"));
        string source=Path.Combine(directory,"source"),data=Path.Combine(directory,"data");
        Directory.CreateDirectory(Path.Combine(source,"parent","tiny","child"));
        File.WriteAllText(Path.Combine(source,"parent","tiny","a.jpg"),"a");
        File.WriteAllText(Path.Combine(source,"parent","tiny","child","b.jpg"),"b");
        await using var catalog=new CatalogStore(data);await catalog.Initialize();
        long epoch=await catalog.OpenRoot("root",source);await new DirectoryIndexer(catalog).Scan("root",source,epoch,true,[],null,CancellationToken.None);
        await catalog.Write(c=>
        {
            using var q=c.CreateCommand();q.CommandText="""
                WITH RECURSIVE n(v) AS(VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<30000)
                INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode)
                SELECT 'unrelated-'||v,'root',(SELECT directory_id FROM Directories WHERE root_id='root' AND relative_path=''),'sibling-'||v,'sibling-'||v,'sibling-'||v,'sensitive' FROM n
                """;return q.ExecuteNonQuery();
        });
        var filter=new FilterSpec{RootId="root",DirectoryScope="parent\\tiny",Grouping=new(true)};
        var snapshot=await catalog.CreateSnapshot(filter,epoch,1);Assert.Equal(2,snapshot.Count);
        Assert.True(await catalog.RetainSnapshot(snapshot.Id));
        async Task<string[]> Paths(string id)
        {
            await using var sessions=new DatabaseExecutor(Path.Combine(data,"sessions.sqlite"));
            return await sessions.Execute(c=>
            {
                using var q=c.CreateCommand();q.CommandText="SELECT relative_path FROM ResultDirectories WHERE session_id=$id ORDER BY relative_path";q.Parameters.AddWithValue("$id",id);
                using var rows=q.ExecuteReader();var paths=new List<string>();while(rows.Read())paths.Add(rows.GetString(0));return paths.ToArray();
            });
        }
        Assert.Equal(new[]{"","parent","parent\\tiny","parent\\tiny\\child"},await Paths(snapshot.Id));
        var whole=await catalog.CreateSnapshot(filter with{DirectoryScope=""},epoch,2);Assert.Equal(30004,(await Paths(whole.Id)).Length);
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE Directories SET relative_path='renamed' WHERE relative_path='parent'";return q.ExecuteNonQuery();});
        Assert.Contains("parent",await Paths(snapshot.Id));Assert.Equal(2,(await catalog.ReadPage(snapshot.Id,0)).Count);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.CreateSnapshot(filter,epoch,3,canceled.Token));
        Assert.Equal(4,(await Paths(snapshot.Id)).Length);Assert.True(await catalog.ReleaseSnapshot(snapshot.Id));
    }
}
