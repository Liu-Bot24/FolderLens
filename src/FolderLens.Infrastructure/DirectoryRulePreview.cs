using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record DirectoryRulePreview(long VisibleDirectories,long HiddenDirectories,long VisibleFiles,long HiddenFiles,string[] HiddenExamples);

public sealed partial class CatalogStore
{
    public Task<DirectoryRulePreview> PreviewDirectoryRules(string rootId,DirectoryRule[] rules,CancellationToken cancellation=default)=>interactiveReader.Execute(c=>
    {
        var matcher=new DirectoryRuleSet(rules);long shown=0,hidden=0,shownFiles=0,hiddenFiles=0;var examples=new List<string>();
        using var cmd=c.CreateCommand();cmd.CommandText="""
            SELECT d.relative_path,coalesce(n.files,0) FROM Directories d
            LEFT JOIN (SELECT directory_id,count(*) files FROM Files WHERE root_id=$root AND entry_state='present' GROUP BY directory_id) n ON n.directory_id=d.directory_id
            WHERE d.root_id=$root AND d.entry_state='present'
            """;cmd.Parameters.AddWithValue("$root",rootId);
        using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();string path=reader.GetString(0);long count=reader.GetInt64(1);
            if(matcher.IsVisible(path)){shown++;shownFiles+=count;}
            else{hidden++;hiddenFiles+=count;if(examples.Count<20)examples.Add(path);}
        }
        return new DirectoryRulePreview(shown,hidden,shownFiles,hiddenFiles,examples.ToArray());
    },cancellation);
}
