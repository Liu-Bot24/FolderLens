namespace FolderLens.Core;

public static class DirectoryBrowseScope
{
    // Exact path casing preserves distinct entries in case-sensitive Windows directories.
    public static string? Relative(string scanRoot,string target)
    {
        string prefix=Path.TrimEndingDirectorySeparator(scanRoot);
        string path=Path.TrimEndingDirectorySeparator(target);
        if(string.Equals(prefix,path,StringComparison.Ordinal))return "";
        prefix=Path.EndsInDirectorySeparator(prefix)?prefix:prefix+Path.DirectorySeparatorChar;
        return path.StartsWith(prefix,StringComparison.Ordinal)?path[prefix.Length..]:null;
    }
}
