using System.Runtime.InteropServices;
using System.Text.Json;

namespace FolderLens.Infrastructure;

public static class NavigationDriveNames
{
    private static string DriveRoot(string root)
    {
        if(root.Length!=3||!char.IsAsciiLetter(root[0])||root[1]!=':'||root[2] is not ('\\' or '/'))throw new ArgumentException("需要磁盘根目录。",nameof(root));
        return char.ToUpperInvariant(root[0])+@":\";
    }
    public static string Format(string root,string? displayName)
    {
        string drive=DriveRoot(root).TrimEnd('\\'),label=(displayName??"").Trim();
        string suffix=" ("+drive+")";
        if(label.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))label=label[..^suffix.Length].TrimEnd();
        int server=label.LastIndexOf(@" (\\",StringComparison.Ordinal);
        if(server>=0&&label.EndsWith(')'))label=label[..server].TrimEnd();
        return label.Length==0||label.Equals(drive,StringComparison.OrdinalIgnoreCase)||label.Equals(root,StringComparison.OrdinalIgnoreCase)?drive:drive+" "+label;
    }
    public static async Task<string> Read(string root,string executable,CancellationToken cancellation)
    {
        root=DriveRoot(root);
        string json=await BoundedProcess.Run(executable,["drive-label",root],TimeSpan.FromSeconds(5),8192,cancellation).ConfigureAwait(false);
        return Format(root,JsonSerializer.Deserialize<string>(json));
    }
    // Run only in the short-lived scan helper: Shell namespace providers can
    // block on an unavailable share. The parent owns the deadline and process.
    public static Task<string> ReadShellName(string root)
    {
        root=DriveRoot(root);var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            bool initialized=false;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0,2));initialized=true;
                if(SHGetFileInfoW(root,0,out var info,(uint)Marshal.SizeOf<ShellFileInfo>(),0x200)==0)throw new IOException("无法读取磁盘名称。");
                done.TrySetResult(info.DisplayName);
            }
            catch(Exception error){done.TrySetException(error);}
            finally{if(initialized)CoUninitialize();}
        }){IsBackground=true,Name="FolderLens drive name"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;public int IconIndex;public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string TypeName;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern nint SHGetFileInfoW(string path,uint attributes,out ShellFileInfo info,uint size,uint flags);
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
}
