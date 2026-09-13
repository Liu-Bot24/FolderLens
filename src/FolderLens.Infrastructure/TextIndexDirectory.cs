using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

internal sealed class TextIndexDirectory : IDisposable
{
    private readonly List<SafeFileHandle> pins=[];
    private readonly string directory;
    private const string Marker=".folderlens-text-index-v1";
    internal const long MaximumIndexBytes=64L*1024*1024;
    private const int MaximumFiles=8;
    public TextIndexDirectory(string directory)
    {
        this.directory=Path.GetFullPath(directory).TrimEnd('\\');
        if(this.directory.StartsWith('\\')||Path.GetPathRoot(this.directory)?.TrimEnd('\\')==this.directory||new DriveInfo(Path.GetPathRoot(this.directory)!).DriveType==DriveType.Network)
            throw new ArgumentException("文本索引必须位于本机的应用专用目录。");
        try
        {
            string current=Path.GetPathRoot(this.directory)!;pins.Add(Open(current,0x80,3,true));
            foreach(string component in this.directory[current.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries))
            {current=Path.Combine(current,component);if(!Directory.Exists(current))Directory.CreateDirectory(current);pins.Add(Open(current,0x80,3,true));}
            string marker=Path.Combine(this.directory,Marker);
            if(!File.Exists(marker))
            {
                if(Directory.EnumerateFileSystemEntries(this.directory).Any())throw new IOException("文本索引目录必须是空目录或已标记的应用索引目录。");
                using var output=new FileStream(Open(marker,0xC0000000,1,false),FileAccess.ReadWrite);output.Write("FolderLens text index v1\n"u8);output.Flush(true);
            }
            using var input=new FileStream(Open(marker,0x80000000,3,false),FileAccess.Read);
            if(input.Length!=25)throw new IOException("文本索引目录标记无效。");
            using var reader=new StreamReader(input);if(reader.ReadToEnd()!="FolderLens text index v1\n")throw new IOException("文本索引目录标记无效。");
        }
        catch{Dispose();throw;}
    }
    public FileStream OpenIndex(string key,CancellationToken cancellation=default)
    {
        if(key.Length!=64 || !key.All(Uri.IsHexDigit))throw new ArgumentException("文本索引键无效。");
        using var gate=AcquireGate(cancellation);
        string path=Path.Combine(directory,key+".flti");
        var files=new DirectoryInfo(directory).EnumerateFiles("*.flti").Where(file=>file.Name.Length==69&&file.Name[..64].All(Uri.IsHexDigit)).OrderBy(file=>file.LastWriteTimeUtc).ToList();
        int target=File.Exists(path)?MaximumFiles:MaximumFiles-1;
        foreach(var file in files.ToArray())
        {
            cancellation.ThrowIfCancellationRequested();
            if(files.Count<=target)break;
            if(string.Equals(file.FullName,path,StringComparison.OrdinalIgnoreCase))continue;
            // Exclusive delete-on-close cannot remove an index held by another reader.
            // The pinned, marked directory and hash-only names contain application cache only.
            try
            {
                if((file.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                using(var retired=new FileStream(Open(file.FullName,0x80010000,3,false,0,deleteOnClose:true),FileAccess.Read)){}
                files.Remove(file);
            }
            catch(IOException){} // An active index stays pinned; try another expired cache.
        }
        if(files.Count>target)throw new IOException("文本索引缓存正在使用，暂不能建立新索引；仍可按字节阅读和搜索。");
        return new FileStream(Open(path,0xC0000000,4,false),FileAccess.ReadWrite,64*1024);
    }
    private SafeFileHandle AcquireGate(CancellationToken cancellation)
    {
        long deadline=Environment.TickCount64+1000;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            try{return Open(Path.Combine(directory,".cache-lock"),0xC0000000,4,false,0);}
            catch(IOException error) when(error.InnerException is Win32Exception {NativeErrorCode:32}&&Environment.TickCount64<deadline){Thread.Sleep(10);}
        }
    }
    private static SafeFileHandle Open(string path,uint access,uint disposition,bool directory,uint? share=null,bool deleteOnClose=false)
    {
        var handle=CreateFileW(path.StartsWith("\\\\?\\")?path:"\\\\?\\"+path,access,share??(directory?3u:1u),IntPtr.Zero,disposition,0x00200000|(directory?0x02000000u:0)|(deleteOnClose?0x04000000u:0),IntPtr.Zero);
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new IOException("无法打开应用文本索引目录或文件。",new Win32Exception(error));}
        if(!GetTag(handle,9,out TagInfo tag,Marshal.SizeOf<TagInfo>())||(tag.Attributes&0x400)!=0){handle.Dispose();throw new IOException("文本索引目录不能经过重解析点。");}
        return handle;
    }
    public void Dispose(){foreach(var pin in pins)pin.Dispose();pins.Clear();}
    [StructLayout(LayoutKind.Sequential)]private struct TagInfo{public uint Attributes,Tag;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetTag(SafeFileHandle handle,int kind,out TagInfo tag,int size);
}
