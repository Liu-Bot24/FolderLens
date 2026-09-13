namespace FolderLens.Core;

public static class FileCategories
{
    private static readonly Dictionary<string,string[]> extensions=new()
    {
        ["pdf"]=["pdf"],
        ["documents"]=["doc","docx","docm","dot","dotx","rtf","odt"],
        ["spreadsheets"]=["xls","xlsx","xlsm","xlsb","csv","ods"],
        ["presentations"]=["ppt","pptx","pptm","pps","ppsx","odp"],
        ["archives"]=["zip","rar","7z","tar","gz","bz2","xz"]
    };
    public static string[] Extensions(string category)=>extensions.TryGetValue(category,out var items)?items.ToArray():[];
    public static string[] Kinds(string category)=>category switch
    {
        "image" or "video" or "audio"=>[category],"media"=>["image","video"],"text"=>["text","markdown"],_=>[]
    };
    public static string FromFilter(FilterSpec filter)
    {
        foreach(var entry in extensions)if(filter.Kinds.Length==0&&entry.Value.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(filter.Extensions))return entry.Key;
        return filter.Kinds.Length==0?"all":filter.Kinds.Contains("image")&&filter.Kinds.Contains("video")?"media":filter.Kinds.Contains("text")||filter.Kinds.Contains("markdown")?"text":filter.Kinds[0];
    }
}
