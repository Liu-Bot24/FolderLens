using System.Text.Json;

namespace FolderLens.App;

internal static class AppPaths
{
    public static async Task<string> DataDirectory(string[] args)
    {
        int index=Array.IndexOf(args,"--data-dir");
        if(index>=0&&index+1<args.Length)return Local(args[index+1]);
        string portable=Path.Combine(AppContext.BaseDirectory,"portable.json");
        if(!File.Exists(portable))return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FolderLens");
        using var config=JsonDocument.Parse(await File.ReadAllTextAsync(portable));string relative=config.RootElement.GetProperty("dataDirectory").GetString()??"data";
        if(Path.IsPathRooted(relative)||relative.Split('\\','/').Any(s=>s is ".." or "."||s.Contains(':')))throw new InvalidDataException("便携数据目录配置无效。");
        return Local(Path.Combine(AppContext.BaseDirectory,relative));
    }
    private static string Local(string path){path=Path.GetFullPath(path);if(new Uri(path).IsUnc||new DriveInfo(Path.GetPathRoot(path)!).DriveType==DriveType.Network)throw new InvalidOperationException("索引和缓存必须保存在本地磁盘。");return path;}
}
