using System.Diagnostics;
using System.Text.Json;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;

namespace FolderLens.Diagnostics;

internal static class GroupingScenario
{
    public static async Task<int> Run(string output,string sourceCatalog,string sourceRoot)
    {
        string data=Path.Combine(output,"index");
        if(Directory.Exists(data))throw new IOException("Choose a fresh grouping output directory.");
        Directory.CreateDirectory(data);
        using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=sourceCatalog,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        using(var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"catalog.sqlite"),Pooling=false}.ToString()))
        {source.Open();destination.Open();source.BackupDatabase(destination);}
        await using var catalog=new CatalogStore(data);await catalog.Initialize();
        string root=await catalog.Read(c=>
        {
            using var command=c.CreateCommand();command.CommandText="SELECT root_id FROM Roots WHERE display_path=$path";command.Parameters.AddWithValue("$path",sourceRoot);
            return command.ExecuteScalar()?.ToString()??throw new InvalidDataException("The selected root is absent from this local index.");
        });
        var runs=new List<object>();long? expected=null;
        foreach(string mode in new[]{"off","first","all"})
        {
            var filter=new FilterSpec{RootId=root,Kinds=["image"],ShowHidden=true,Sort=new("logicalBytes","desc"),Grouping=new(mode!="off",mode=="off"?"all":mode)};
            var watch=Stopwatch.StartNew();var handle=await catalog.CreateSnapshot(filter,1,runs.Count+1);double createMs=watch.Elapsed.TotalMilliseconds;
            expected??=handle.Count;if(handle.Count!=expected)throw new InvalidDataException("Grouping changed the file count.");
            watch.Restart();var groups=await catalog.ReadGroups(handle.Id);double groupsMs=watch.Elapsed.TotalMilliseconds;
            if(mode!="off"&&groups.Sum(g=>g.Count)!=handle.Count)throw new InvalidDataException("Group spans do not cover the result.");
            watch.Restart();var last=await catalog.ReadPage(handle.Id,Math.Max(0,handle.Count-256));double lastPageMs=watch.Elapsed.TotalMilliseconds;
            if(last.Count!=Math.Min(256,handle.Count))throw new InvalidDataException("Deep page incomplete.");
            runs.Add(new{mode,count=handle.Count,groups=groups.Count,createMs,groupsMs,lastPageMs,sessionBytes=Directory.GetFiles(data,"sessions.sqlite*").Sum(path=>new FileInfo(path).Length),processPeakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64});
            Console.WriteLine($"{mode}: {handle.Count} images, {groups.Count} groups, build {createMs:F0} ms, deep page {lastPageMs:F1} ms");
            await catalog.ReleaseSnapshot(handle.Id);
        }
        await File.WriteAllTextAsync(Path.Combine(output,"grouping-result.json"),JsonSerializer.Serialize(new{source="read-only backup of existing local catalogue; source media not accessed",ui="NOT_RUN",runs},new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
}
