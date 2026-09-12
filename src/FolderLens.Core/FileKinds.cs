namespace FolderLens.Core;

public static class FileKinds
{
    public static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase){".arw",".dng",".cr2",".cr3",".nef",".raf",".rw2",".orf",".pef"};
    private static readonly HashSet<string> Images=new(StringComparer.OrdinalIgnoreCase){".jpg",".jpeg",".jpe",".png",".apng",".gif",".webp",".bmp",".tif",".tiff",".ico",".heic",".heif",".avif",".jxl"};
    private static readonly HashSet<string> Videos=new(StringComparer.OrdinalIgnoreCase){".mp4",".mkv",".mov",".avi",".webm",".wmv",".m4v"};
    private static readonly HashSet<string> Audio=new(StringComparer.OrdinalIgnoreCase){".mp3",".wav",".flac",".aac",".m4a",".ogg",".opus",".wma"};
    private static readonly HashSet<string> Text=new(StringComparer.OrdinalIgnoreCase){".txt",".log",".csv",".json",".xml",".cs",".cpp",".c",".h",".py",".js",".ts",".html",".css",".yaml",".yml",".ini",".toml",".sql"};
    public static string Candidate(string path)
    {
        string ext=Path.GetExtension(path);
        if(Images.Contains(ext)||Raw.Contains(ext))return "image";
        if(Videos.Contains(ext))return "video";
        if(Audio.Contains(ext))return "audio";
        if(ext.Equals(".md",StringComparison.OrdinalIgnoreCase)||ext.Equals(".markdown",StringComparison.OrdinalIgnoreCase))return "markdown";
        return Text.Contains(ext)?"text":"other";
    }
}
