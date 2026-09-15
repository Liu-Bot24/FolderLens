using FolderLens.Core;
using Microsoft.VisualBasic.FileIO;

namespace FolderLens.Infrastructure;

public enum FileOperationKind { Rename, Move, Recycle }
public sealed record FileOperationRequest(string Source,SourceFileStamp Stamp,FileOperationKind Kind,string? Destination=null,string? PhysicalIdentity=null);

public static class FileOperations
{
    public static string RenameTarget(string source,string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name!=name.Trim()||name.EndsWith('.')||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name is "." or "..")
            throw new ArgumentException("请输入有效的文件名，不要包含路径或末尾空格、句点。");
        string stem=name.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem,StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("此名称是 Windows 保留名称，请换一个名称。");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,name);
    }

    // Compatibility entry for non-UI callers; all filesystem mutations still use the Shell.
    public static async Task Execute(FileOperationRequest request,CancellationToken cancellation=default)
    {
        var action=request.Kind==FileOperationKind.Rename?ShellFileAction.Rename:request.Kind==FileOperationKind.Move?ShellFileAction.Move:ShellFileAction.Recycle;
        var shell=new ShellFileRequest(request.Source,action,request.Destination is null?null:Path.GetDirectoryName(request.Destination),request.Destination is null?null:Path.GetFileName(request.Destination),request.Stamp,request.PhysicalIdentity);
        var result=await ShellFileOperations.ExecuteCore([shell],0,cancellation,true);
        if(result.Items.Single().Outcome!=ShellItemOutcome.Completed)throw new IOException("文件操作未完整完成，请检查源位置和目标位置。");
    }
}
