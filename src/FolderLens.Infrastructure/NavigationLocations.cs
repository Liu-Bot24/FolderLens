using System.Runtime.InteropServices;

namespace FolderLens.Infrastructure;

public sealed record NavigationLocation(string Path,string Label);
public sealed record NavigationLocations(NavigationLocation[] Places,string[] Drives)
{
    public static NavigationLocations Read()
    {
        string Folder(Environment.SpecialFolder folder)=>Environment.GetFolderPath(folder,Environment.SpecialFolderOption.DoNotVerify);
        var places=new List<NavigationLocation>{new(Folder(Environment.SpecialFolder.DesktopDirectory),"桌面")};
        Guid downloads=new("374DE290-123F-4565-9164-39C4925E467B");
        int result=SHGetKnownFolderPath(ref downloads,0x4000,IntPtr.Zero,out var memory);
        try
        {
            if(result>=0&&Marshal.PtrToStringUni(memory) is {Length:>0} path)places.Add(new(path,"下载"));
            else Console.Error.WriteLine($"Downloads location unavailable: 0x{result:X8}");
        }
        finally{Marshal.FreeCoTaskMem(memory);}
        places.AddRange(new[]{new NavigationLocation(Folder(Environment.SpecialFolder.MyDocuments),"文档"),
            new NavigationLocation(Folder(Environment.SpecialFolder.MyPictures),"图片"),new NavigationLocation(Folder(Environment.SpecialFolder.MyMusic),"音乐"),
            new NavigationLocation(Folder(Environment.SpecialFolder.MyVideos),"视频"),new NavigationLocation(Folder(Environment.SpecialFolder.UserProfile),"用户文件夹")});
        // Only read registered drive letters. Do not probe volume labels/readiness or
        // enumerate network locations while creating the navigation surface.
        return new(places.Where(place=>place.Path.Length>0).ToArray(),Environment.GetLogicalDrives());
    }
    [DllImport("shell32.dll",ExactSpelling=true)]private static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
}
