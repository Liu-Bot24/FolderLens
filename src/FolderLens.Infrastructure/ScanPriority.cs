namespace FolderLens.Infrastructure;

/// <summary>Thread-safe navigation hint; it changes scheduling, never the scan's inclusion policy.</summary>
public sealed class ScanPriority
{
    private string relativePath="";
    public string RelativePath=>Volatile.Read(ref relativePath);
    public void Prefer(string path)
    {
        if(Path.IsPathRooted(path)||path.Contains(':')||path.Split('\\','/').Any(p=>p is "." or ".."))throw new ArgumentException("优先目录必须在扫描根目录内。",nameof(path));
        Volatile.Write(ref relativePath,path.Replace('/','\\').TrimEnd('\\'));
    }
    internal static string Branch(string path){int separator=path.IndexOf('\\');return separator<0?path:path[..separator];}
}
