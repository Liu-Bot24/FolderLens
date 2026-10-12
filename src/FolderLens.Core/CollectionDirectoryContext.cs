namespace FolderLens.Core;

public sealed class LegacyCollectionRuleContextException():ArgumentException("旧收藏夹路径规则没有记录原根目录，请先确认原根目录后再应用。");

/// <summary>V2 collection rules use the source directory, independent of which
/// ordinary or playlist observation currently supplies the file metadata.</summary>
public static class CollectionDirectoryContext
{
    public static string SourceDirectory(string root,string relativeDirectory)=>Normalize(Path.Combine(root,relativeDirectory));
    public static string Normalize(string path)
    {
        path=PathRules.ValidateSource(path);
        if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))path=@"\\"+path[8..];
        else if(path.StartsWith(@"\\?\",StringComparison.Ordinal)&&path.Length>5&&path[5]==':')path=path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }
    public static string? RelativeTo(string sourceDirectory,string root)
    {
        string path=Normalize(sourceDirectory),basis=Normalize(root);
        if(path.Equals(basis,StringComparison.OrdinalIgnoreCase))return "";
        string prefix=basis.TrimEnd('\\')+"\\";
        return path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?path[prefix.Length..]:null;
    }
}
