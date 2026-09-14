using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public static class LocalResourceRules
{
    public static string ResolveImage(string root,string document,string url)
    {
        string decoded=Uri.UnescapeDataString(url);
        if(Uri.TryCreate(decoded,UriKind.Absolute,out _) || decoded.StartsWith('\\') || decoded.Contains(':') || decoded.Split('/','\\').Any(p=>p==".."))throw new UnauthorizedAccessException("不允许此 Markdown 资源路径。");
        string candidate=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!,decoded.Replace('/',Path.DirectorySeparatorChar)));
        if(FileAllocation.IsDeferred((long)File.GetAttributes(candidate)))throw new UnauthorizedAccessException("内嵌图片仅在线；请先单独打开并确认读取该图片。");
        using var rootHandle=CreateFileW(Path.GetFullPath(root),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(rootHandle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        // Metadata-only, no-recall handle. Resolving the boundary must not itself
        // read content or cause an online placeholder to download.
        using var imageHandle=CreateFileW(candidate,0,7,IntPtr.Zero,3,0x02100000,IntPtr.Zero);if(imageHandle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        string actualRoot=FinalName(rootHandle).TrimEnd('\\'),actualImage=FinalName(imageHandle);
        if(!actualImage.StartsWith(actualRoot+"\\",StringComparison.Ordinal))throw new UnauthorizedAccessException("Markdown 图片不在当前根目录内。");
        string extension=Path.GetExtension(actualImage).ToLowerInvariant();if(extension is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp"))throw new NotSupportedException("该格式不用于 Markdown 内嵌预览。");
        return actualImage;
    }
    private static string FinalName(SafeFileHandle handle)
    {
        var buffer=new StringBuilder(32768);uint size=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,0);if(size==0||size>=buffer.Capacity)throw new Win32Exception(Marshal.GetLastWin32Error());return buffer.ToString();
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
}
