namespace FolderLens.Core;

public static class FileTypeDisplay
{
    public static string Label(string name,string kind)
    {
        string extension=Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if(extension=="pdf")return "PDF 文档";
        if(extension=="odt")return "OpenDocument 文档";
        if(extension=="ods")return "OpenDocument 表格";
        if(extension=="odp")return "OpenDocument 演示文稿";
        if(extension=="rtf")return "RTF 文档";
        if(FileCategories.Extensions("documents").Contains(extension))return "Word 文档";
        if(FileCategories.Extensions("spreadsheets").Contains(extension))return extension=="csv"?"CSV 表格":"Excel 表格";
        if(FileCategories.Extensions("presentations").Contains(extension))return "演示文稿";
        if(FileCategories.Extensions("archives").Contains(extension))return "压缩文件";
        return kind switch{"image"=>"图片","video"=>"视频","audio"=>"音频","markdown"=>"Markdown 文档","text"=>"文本文件",_=>extension.Length==0?"文件":extension.ToUpperInvariant()+" 文件"};
    }
    public static string Badge(string name,string kind)
    {
        string extension=Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return extension.Length is >0 and <=8?extension:kind=="audio"?"音频":"文件";
    }
}
