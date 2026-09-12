namespace FolderLens.Core;

public readonly record struct WorkContext(long RootEpoch, long QueryGeneration, long SelectionGeneration, string EntryId, long FileVersion, Guid WorkerInstanceId)
{
    public bool IsCurrent(WorkContext active) => this == active;
}

public enum FieldState { NotRequested, Pending, Ready, Failed, Unsupported, DeferredOffline }
public enum MatchState { Match, NoMatch, Pending, Unresolvable }

public static class PathRules
{
    // Deliberately ordinal: uncertain Windows case mode must never merge distinct entries.
    public static bool IsWithinRelative(string candidate, string directory) =>
        candidate.Equals(directory, StringComparison.Ordinal) ||
        candidate.StartsWith(directory.TrimEnd('\\', '/') + "\\", StringComparison.Ordinal);

    public static string ValidateSource(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择普通文件或文件夹路径。");
        string full = Path.GetFullPath(path);
        string tail = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
        if (tail.IndexOf(':', tail.Length > 1 && tail[1] == ':' ? 2 : 0) >= 0)
            throw new ArgumentException("不支持设备路径或备用数据流。");
        return full;
    }
}
