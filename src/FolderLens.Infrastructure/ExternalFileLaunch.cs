using System.Diagnostics;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record ExternalFileTarget(string Path,string Kind);
public static class ExternalFileLaunch
{
    public static async Task<ExternalFileTarget> Resolve(CatalogStore catalog,SourceFileProbe probe,string root,string rootId,string entryId,long version,bool allowCloud,CancellationToken cancellation)
    {
        var file=await catalog.ReadFileProperties(rootId,entryId,version,cancellation).ConfigureAwait(false)??throw new IOException("文件已变化，请刷新后重新打开。");
        if(file.EntryState!="present"||!FileCategories.SupportsExternalOpen(file.Name,file.Kind))throw new IOException("此文件当前不可打开，请刷新或定位原文件。");
        string basePath=Path.GetFullPath(root).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        string path=PathRules.ValidateSource(Path.GetFullPath(Path.Combine(basePath,file.RelativePath)));
        if(!path.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("文件路径已超出当前根目录。");
        var observed=await probe.Read(path,cancellation,allowCloud).ConfigureAwait(false);
        if(observed.SourceSignature!=file.SourceSignature||file.HydrationState=="placeholder")
        {
            file=await catalog.ResolveFileRead(rootId,entryId,version,probe,allowCloud,cancellation).ConfigureAwait(false);
            observed=await probe.Read(path,cancellation,allowCloud).ConfigureAwait(false);
        }
        if(observed!=new SourceFileStamp(file.LogicalBytes,file.ModifiedUtcTicks,file.SourceSignature))throw new IOException("原文件已变化，请刷新后重新打开。");
        var latest=await catalog.ReadFileProperties(rootId,entryId,version,cancellation).ConfigureAwait(false);
        if(latest is null||latest.EntryState!="present"||latest.RelativePath!=file.RelativePath||latest.LogicalBytes!=file.LogicalBytes||latest.ModifiedUtcTicks!=file.ModifiedUtcTicks||latest.Kind!=file.Kind)throw new IOException("文件在打开前发生变化，请重试。");
        cancellation.ThrowIfCancellationRequested();return new(path,file.Kind);
    }
    public static ProcessStartInfo CreateStartInfo(string path,string? executable,params string[] privateDirectories)
    {
        path=PathRules.ValidateSource(path);if(path.Any(char.IsControl))throw new ArgumentException("文件路径包含无效字符。");
        if(string.IsNullOrWhiteSpace(executable))return new(path){UseShellExecute=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};
        executable=PathRules.ValidateSource(executable);if(!executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择播放器 EXE。");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)!};start.ArgumentList.Add(path);
        bool IsPrivate(string value)
        {
            if(!Path.IsPathFullyQualified(value))return false;
            try
            {
                string full=Path.GetFullPath(value).TrimEnd('\\','/');
                return privateDirectories.Any(directory=>full.Equals(Path.GetFullPath(directory).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)||full.StartsWith(Path.GetFullPath(directory).TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
            }
            catch(ArgumentException){return false;}
            catch(NotSupportedException){return false;}
        }
        foreach(string key in new[]{"DOTNET_ROOT","DOTNET_ROOT_X64","DOTNET_ROOT_X86","DOTNET_ADDITIONAL_DEPS","DOTNET_SHARED_STORE","DOTNET_STARTUP_HOOKS","DOTNET_BUNDLE_EXTRACT_BASE_DIR","WEBVIEW2_BROWSER_EXECUTABLE_FOLDER","WEBVIEW2_USER_DATA_FOLDER"})
            if(start.Environment.TryGetValue(key,out string? value)&&value is not null&&value.Split(';',StringSplitOptions.RemoveEmptyEntries).Any(IsPrivate))start.Environment.Remove(key);
        if(start.Environment.TryGetValue("PATH",out string? search)&&search is not null)start.Environment["PATH"]=string.Join(';',search.Split(';').Where(part=>!IsPrivate(part.Trim('"'))));
        return start;
    }
}
