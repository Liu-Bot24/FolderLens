namespace FolderLens.Core;

/// <summary>DIP-based, bounded text card layout. No document parsing or file reads.</summary>
public readonly record struct TextThumbnailLayout(int Lines,int Characters,int ReadBytes)
{
    public const double FontSize=13,LineHeight=18;
    public static TextThumbnailLayout ForWidth(double width)
    {
        if(!double.IsFinite(width)||width<144)return new(0,0,0);
        width=Math.Min(width,260);
        int lines=Math.Max(1,(int)((Math.Round(width*.68)-26)/LineHeight));
        int characters=Math.Clamp((int)((width-34)/FontSize)*lines*2,16,256);
        int bytes=Math.Min(1024,((characters*4+4+255)/256)*256);
        return new(lines,characters,bytes);
    }
    public string Display(string prefix)
    {
        prefix=prefix.Replace("\t","  ");
        int length=Math.Min(Characters,prefix.Length);
        if(length>0&&length<prefix.Length&&char.IsHighSurrogate(prefix[length-1]))length--;
        return prefix[..length];
    }
}
