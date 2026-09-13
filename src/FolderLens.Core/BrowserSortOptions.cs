namespace FolderLens.Core;

public static class BrowserSortOptions
{
    public static bool IsApplicable(string category,string field)=>field switch
    {
        "width" or "height" or "pixelCount"=>category is "image" or "video" or "media" or "all",
        "durationMs"=>category is "video" or "audio" or "media" or "all",
        "name" or "path" or "format" or "logicalBytes" or "modified"=>true,
        _=>false
    };
}
