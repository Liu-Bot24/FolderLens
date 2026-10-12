using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record DirectoryRulePreview(long VisibleDirectories,long HiddenDirectories,long VisibleFiles,long HiddenFiles,string[] HiddenExamples);

public sealed partial class CatalogStore
{
    public Task<DirectoryRulePreview> PreviewDirectoryRules(string rootId,DirectoryRule[] rules,CancellationToken cancellation=default,string? collectionId=null)=>interactiveReader.Execute(c=>
    {
        new FilterSpec{SchemaVersion=2,RootId=rootId,CollectionId=collectionId,DirectoryRules=rules}.Validate();
        RegisterFunctions(c);
        bool sourceContext=collectionId is not null||rules.Any(rule=>rule.Target=="sourcePath"||rule.BasePath is not null);
        var matcher=new DirectoryRuleSet(rules);long shown=0,hidden=0,shownFiles=0,hiddenFiles=0;var examples=new List<string>();
        using var cmd=c.CreateCommand();cmd.CommandText="""
            SELECT d.relative_path,coalesce(n.files,0),lens_source_directory(r.display_path,d.relative_path) FROM Directories d JOIN Roots r ON r.root_id=d.root_id
            LEFT JOIN (SELECT directory_id,count(*) files FROM Files WHERE root_id=$root AND entry_state='present' GROUP BY directory_id) n ON n.directory_id=d.directory_id
            WHERE d.root_id=$root AND d.entry_state='present'
            """;cmd.Parameters.AddWithValue("$root",rootId);
        if(!sourceContext)cmd.CommandText="SELECT d.relative_path,coalesce(n.files,0),NULL FROM Directories d LEFT JOIN (SELECT directory_id,count(*) files FROM Files WHERE root_id=$root AND entry_state='present' GROUP BY directory_id) n ON n.directory_id=d.directory_id WHERE d.root_id=$root AND d.entry_state='present'";
        if(collectionId is not null)
        {
            var query=FilterSql.Build(new(){RootId=rootId,CollectionId=collectionId,Kinds=[],ShowHidden=true});
            // Normalize once per observed directory, then merge observations of
            // the same source directory without repeating path work per file.
            cmd.CommandText=$"SELECT source,sum(files),source FROM (SELECT lens_source_directory(r.display_path,d.relative_path) source,n.files FROM (SELECT f.directory_id,count(*) files FROM Files f WHERE {query.MatchExpression} GROUP BY f.directory_id) n JOIN Directories d ON d.directory_id=n.directory_id JOIN Roots r ON r.root_id=d.root_id) GROUP BY source";
            foreach(var parameter in query.Parameters)cmd.Parameters.AddWithValue(parameter.Key,parameter.Value);
        }
        using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();string path=reader.GetString(0);long count=reader.GetInt64(1);
            if(matcher.IsVisible(path,reader.IsDBNull(2)?null:reader.GetString(2))){shown++;shownFiles+=count;}
            else{hidden++;hiddenFiles+=count;if(examples.Count<20)examples.Add(path);}
        }
        return new DirectoryRulePreview(shown,hidden,shownFiles,hiddenFiles,examples.ToArray());
    },cancellation);
}
