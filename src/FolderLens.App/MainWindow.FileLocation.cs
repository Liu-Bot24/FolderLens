using FolderLens.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private string SourceRootId(FileRow row)=>row.Item?.SourceRootId??rootId;
    private long SourceRootEpoch(FileRow row)=>row.Item?.SourceRootEpoch??epoch;
    private string SourceRootPath(FileRow row)=>row.Item?.SourceRootPath??root;
    private string SourcePath(FileRow row)=>Path.Combine(SourceRootPath(row),row.RelativePath);
    private string? BrowserPath(FileRow? row)=>row is null?null:activeCollectionId is null?row.RelativePath:row.NavigationPath;
}
