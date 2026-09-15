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

    // One user operation at a time; native shell calls run on a dedicated STA thread.
    // Cancellation is checked before dispatch, never reported as success mid-operation.
    public static Task Execute(FileOperationRequest request,CancellationToken cancellation=default)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                string source=PathRules.ValidateSource(request.Source);
                var file=new FileInfo(source);
                if(!file.Exists)throw new FileNotFoundException("文件已不存在，请刷新目录。",source);
                if((file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("暂不操作链接或在线占位文件，请在资源管理器中操作。");
                if(request.PhysicalIdentity is null||FileAllocation.InspectMetadata(source).PhysicalIdentity!=request.PhysicalIdentity)throw new IOException("文件身份已变化或无法核实，请刷新后重试。");
                if(new SourceFileStamp(file.Length,file.LastWriteTimeUtc.Ticks)!=request.Stamp)throw new IOException("文件已变化，请刷新后重试。");
                if(request.Kind==FileOperationKind.Recycle)
                    FileSystem.DeleteFile(source,UIOption.AllDialogs,RecycleOption.SendToRecycleBin,UICancelOption.ThrowException);
                else
                {
                    string destination=PathRules.ValidateSource(request.Destination??throw new ArgumentException("请选择目标位置。"));
                    if(string.Equals(source,destination,StringComparison.Ordinal))throw new IOException("文件已经位于此位置。");
                    if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("目标位置已有同名文件，请换一个名称或位置。");
                    // File.Move never overwrites an existing target, including a target
                    // created between our check and the actual operation.
                    File.Move(source,destination,overwrite:false);
                }
                done.SetResult();
            }
            catch(OperationCanceledException ex){done.SetCanceled(ex.CancellationToken);}
            catch(Exception ex){done.SetException(ex);}
        }){IsBackground=true,Name="FolderLens file operation"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
}
