namespace FolderLens.Core;

public sealed record PreviewBookmark(long Bytes,long ModifiedUtcTicks,string Kind,int ImagePage=0,long TextOffset=0,string? Encoding=null,double TextScroll=0,bool RenderedMarkdown=false)
{
    public PreviewBookmark? ForFile(long bytes,long modifiedUtcTicks,string kind)
    {
        if(Bytes!=bytes||ModifiedUtcTicks!=modifiedUtcTicks||Kind!=kind||bytes<0)return null;
        return this with
        {
            ImagePage=Math.Max(0,ImagePage),TextOffset=Math.Clamp(TextOffset,0,Math.Max(0,bytes-1)),
            Encoding=Encoding is "utf-8" or "utf-16" or "utf-16BE" or "gb18030" or "gb2312"?Encoding:null,
            TextScroll=double.IsFinite(TextScroll)?Math.Max(0,TextScroll):0
        };
    }
}
