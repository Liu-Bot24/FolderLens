namespace FolderLens.Core;

public static class DetailColumnDefaults
{
    public static bool IsVisible(string category,string field)=>field switch
    {
        "name" or "path" or "format" or "logicalBytes" or "modified"=>true,
        "pixelCount"=>category is "image" or "video" or "media",
        "durationMs"=>category is "audio" or "video" or "media",
        _=>false
    };
}
