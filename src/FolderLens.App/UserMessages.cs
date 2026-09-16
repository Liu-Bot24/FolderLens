using System.ComponentModel;
using System.Text.RegularExpressions;

namespace FolderLens.App;

// Presentation only: preserve exception types/codes for retry and diagnostic paths.
public static class UserMessages
{
    public static string Error(Exception error)
    {
        string message=error.Message.Split('\n')[0].Trim();
        string? known=message switch
        {
            "FileChanged"=>"文件已更改，请刷新后重试。",
            "UnsupportedCodec"=>"暂不支持此文件的编码，可用其他应用打开。",
            "DecodeFailed" or "ProbeFailed"=>"此次未能读取文件内容，请重新打开重试。",
            "Timeout"=>"读取超时，请稍后重试。",
            "ResourceLimit" or "OutOfMemory"=>"可用资源不足，请关闭不需要的预览后重试。",
            "DeferredOffline" or "SourceIoError"=>"文件暂时无法访问，请检查磁盘或网络连接后重试。",
            "SourceMissing" or "NotFound"=>"找不到文件，请刷新文件夹。",
            "AccessDenied" or "TextAccessDenied"=>"没有读取权限，请检查文件或文件夹的访问权限。",
            _=>null
        };
        if(known is not null)return known;
        if(error is Microsoft.Data.Sqlite.SqliteException {SqliteErrorCode:13})return "临时浏览数据已达到空间上限或磁盘已满。已显示的文件仍可浏览；关闭应用可清理临时数据，重新打开后请选择更小的文件夹。";
        if(error is UnauthorizedAccessException)return "没有访问权限，请检查文件或文件夹的权限。";
        if(error is FileNotFoundException or DirectoryNotFoundException)return "找不到文件或文件夹，请检查其是否已移动或删除，然后刷新。";
        if(error is TimeoutException)return "操作超时，请稍后重试。";
        if(error is OutOfMemoryException)return "可用内存不足，请关闭不需要的预览后重试。";
        if(error is Win32Exception win32)return win32.NativeErrorCode switch
        {
            2 or 3=>"找不到文件或文件夹，请刷新后重试。",
            5=>"没有访问权限，请检查文件或文件夹的权限。",
            32 or 33=>"文件正被其他程序占用，请稍后重试。",
            112=>"磁盘空间不足，请释放空间后重试。",
            21 or 53 or 64 or 67 or 1167 or 1231=>"磁盘或网络位置暂时无法访问，请检查连接后重试。",
            _=>"系统未能完成此操作，请重试。"
        };
        // Keep actionable application validation, but not raw protocol, stack or SQL text.
        if(message.Length is >0 and <=240 && Regex.IsMatch(message,"[\u4e00-\u9fff]") &&
           !Regex.IsMatch(message,@"[A-Za-z]:[\\/]|\\\\|Exception|\bSQL\b|\bABI\b|ErrorCode|ProbeFailed|工作进程|预算|版本戳|快照|管线|组件身份",RegexOptions.IgnoreCase))return message;
        return error switch
        {
            ArgumentException or FormatException or OverflowException=>"输入内容无效，请检查填写的条件后重试。",
            InvalidDataException=>"此次未能读取文件内容，请重新打开重试。",
            IOException=>"读写失败，请检查文件是否可访问以及磁盘空间是否充足。",
            _=>"操作未能完成，请重试；若仍失败，请重新打开应用。"
        };
    }

    public static string Results(long displayed,long pending,long failed,bool includesPending)
    {
        var parts=new List<string>{$"已显示 {displayed:N0} 个文件"};
        if(includesPending)parts.Add("仅显示尚未判定的文件");
        if(pending>0)parts.Add($"{pending:N0} 个文件的信息尚未读全，暂不能确定是否符合筛选条件");
        if(failed>0)parts.Add($"{failed:N0} 个文件的信息无法读取，暂不能确定是否符合筛选条件");
        return string.Join(" · ",parts);
    }
}
