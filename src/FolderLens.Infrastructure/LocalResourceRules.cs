using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FolderLens.Contracts;

namespace FolderLens.Infrastructure;

public static class LocalResourceRules
{
    public static string ResolveImage(string root,string document,string url)
    {
        // Path-only lookup is for presentation/validation. Consumers must retain
        // AuthorizeImage's lease rather than establishing a new stamp afterward.
        using var authorized=AuthorizeImage(root,document,url);return authorized.Path;
    }
    public static SourceImageLease AuthorizeImage(string root,string document,string url)
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
        string extension=Path.GetExtension(actualImage).ToLowerInvariant();if(extension is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp")&&!LocalMediaRange.IsVideo(actualImage))throw new NotSupportedException("该格式不用于 Markdown 内嵌预览。");
        var rootIdentity=FileAllocation.InspectMetadata(rootHandle).PhysicalIdentity;
        if(rootIdentity is null)throw new IOException("CannotVerifyMarkdownRoot");
        var pins=new List<SafeFileHandle>();
        try
        {
            string parent=Path.GetDirectoryName(actualImage)!,current=Path.GetPathRoot(parent)!;
            bool rootPinned=false;
            void PinDirectory(string directory)
            {
                if(pins.Count>=127)throw new IOException("MarkdownPathDepthLimit");
                var pin=PinSource(directory,true);pins.Add(pin);
                if(directory.TrimEnd('\\')==actualRoot)
                {
                    if(FileAllocation.InspectMetadata(pin).PhysicalIdentity!=rootIdentity)throw new IOException("FileChanged");
                    rootPinned=true;
                }
            }
            PinDirectory(current);
            foreach(string part in parent[current.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries)){current=Path.Combine(current,part);PinDirectory(current);}
            if(!rootPinned)throw new UnauthorizedAccessException("Markdown 图片不在当前根目录内。");
            var image=PinSource(actualImage,false);pins.Add(image);
            if(FinalName(image)!=actualImage)throw new IOException("FileChanged");
            var observation=FileReadObservation.Read(image);
            return new(actualImage,new(observation.Length,observation.ModifiedUtcTicks,observation.Signature),pins);
        }
        catch{foreach(var pin in pins)pin.Dispose();throw;}
    }
    private static SafeFileHandle PinSource(string path,bool directory)
    {
        // Directory traversal access enforces delete sharing without demanding
        // listing permissions. The file itself cannot be rewritten or unlinked.
        // Every directory in this pinned chain therefore remains nonempty.
        var handle=CreateFileW(path,directory?0xA0u:0x80000080u,directory?3u:1u,IntPtr.Zero,3,0x02300000,IntPtr.Zero);
        if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}
        var info=FileAllocation.InspectMetadata(handle);
        if(info.Attributes is not {} attributes||((attributes&16)!=0)!=directory||
            (attributes&1024)!=0&&!FileAllocation.IsCloudTag(info.ReparseTag??0)||!directory&&FileAllocation.IsDeferred(attributes))
        {handle.Dispose();throw new UnauthorizedAccessException("Markdown 媒体的目录或在线状态已变化，请重试。");}
        return handle;
    }
    private static string FinalName(SafeFileHandle handle)
    {
        var buffer=new StringBuilder(32768);uint size=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,0);if(size==0||size>=buffer.Capacity)throw new Win32Exception(Marshal.GetLastWin32Error());return buffer.ToString();
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
}
