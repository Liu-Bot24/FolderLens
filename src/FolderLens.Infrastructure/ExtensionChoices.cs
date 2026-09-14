using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record ExtensionChoices(string[] Values,bool Truncated);

public sealed partial class CatalogStore
{
    public Task<ExtensionChoices> ReadFileExtensions(FilterSpec scope,CancellationToken cancellation=default)=>interactiveReader.Execute(c=>
    {
        // A directory/category facet must remain available even when another filter
        // has no matches or metadata has not been decoded yet.
        var filter=new FilterSpec{RootId=scope.RootId,ObservedRootEpoch=scope.ObservedRootEpoch,CollectionId=scope.CollectionId,IncludeCollections=scope.IncludeCollections,ExcludeCollections=scope.ExcludeCollections,DirectoryScope=scope.DirectoryScope,ScopeDirectFiles=scope.ScopeDirectFiles,
            Recursive=scope.Recursive,MaxFolderLevels=scope.MaxFolderLevels,Kinds=scope.Kinds,Extensions=scope.Extensions,ShowHidden=scope.ShowHidden,
            Exclusions=scope.Exclusions,DirectoryRules=scope.DirectoryRules};
        var query=FilterSql.Build(filter);
        using var command=c.CreateCommand();command.CommandTimeout=5;
        command.CommandText=$"SELECT DISTINCT lower(ltrim(f.extension,'.')) AS extension FROM Files f WHERE {query.MatchExpression} ORDER BY extension LIMIT 1025";
        foreach(var parameter in query.Parameters)command.Parameters.AddWithValue(parameter.Key,parameter.Value);
        using var rows=command.ExecuteReader();var values=new List<string>();
        while(rows.Read()){cancellation.ThrowIfCancellationRequested();values.Add(rows.GetString(0));}
        return new ExtensionChoices(values.Take(1024).ToArray(),values.Count>1024);
    },cancellation);
}
