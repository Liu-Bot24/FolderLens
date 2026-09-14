using System.Text.Json;

namespace FolderLens.App;

internal static class AppPaths
{
    public static async Task<string> DataDirectory(string[] args)
    {
        string directory=await ResolveDataDirectory(args);
        // This runs before the instance broker or catalog can create any files.
        if(args.Contains("--verify-refresh"))
        foreach(string option in new[]{"--verify-image-switch","--verify-gallery","--verify-category-switch","--verify-bitmap-assets"})
        {
            int index=Array.IndexOf(args,option);if(index<0)continue;
            if(index+1>=args.Length)throw new ArgumentException("缺少只读源目录。");
            string source=Path.GetFullPath(args[index+1]).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string data=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(data.Equals(source,StringComparison.OrdinalIgnoreCase)||data.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("验证输出必须位于源目录以外。");
        }
        return directory;
    }
    private static async Task<string> ResolveDataDirectory(string[] args)
    {
        int index=Array.IndexOf(args,"--data-dir");
        if(index>=0&&index+1<args.Length)return Local(args[index+1]);
        string portable=Path.Combine(AppContext.BaseDirectory,"portable.json");
        if(!File.Exists(portable))return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FolderLens");
        using var config=JsonDocument.Parse(await File.ReadAllTextAsync(portable));string relative=config.RootElement.GetProperty("dataDirectory").GetString()??"data";
        if(Path.IsPathRooted(relative)||relative.Split('\\','/').Any(s=>s is ".." or "."||s.Contains(':')))throw new InvalidDataException("便携数据目录配置无效。");
        return Local(Path.Combine(AppContext.BaseDirectory,relative));
    }
    private static string Local(string path){path=Path.GetFullPath(path);if(new Uri(path).IsUnc||new DriveInfo(Path.GetPathRoot(path)!).DriveType==DriveType.Network)throw new InvalidOperationException("应用数据目录不能放在网络磁盘上，请选择本机磁盘。");return path;}
}
