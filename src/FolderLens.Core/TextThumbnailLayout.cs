namespace FolderLens.Core;

/// <summary>DIP-based, bounded text card layout. No document parsing or file reads.</summary>
public readonly record struct TextThumbnailLayout(int Lines,int Characters,int ReadBytes)
{
    public const double FontSize=12,LineHeight=16;
    public static TextThumbnailLayout ForWidth(double width)
    {
        if(!double.IsFinite(width)||width<144)return new(0,0,0);
        width=Math.Min(width,320);
        int lines=Math.Max(1,(int)((Math.Round(width*.68)-26)/LineHeight));
        int characters=Math.Clamp((int)((width-34)/FontSize)*lines*2,16,512);
        int bytes=Math.Min(2048,((characters*4+4+255)/256)*256);
        return new(lines,characters,bytes);
    }
    public string Display(string prefix)
    {
        var text=new System.Text.StringBuilder(Characters);
        using var lines=new System.IO.StringReader(prefix);
        while(text.Length<Characters&&lines.ReadLine() is {} line)
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            if(text.Length>0)
            {
                if(text.Length+1>=Characters)break;
                text.Append('\n');
            }
            line=line.Replace("\t","  ");
            text.Append(line.AsSpan(0,Math.Min(line.Length,Characters-text.Length)));
        }
        if(text.Length>0&&char.IsHighSurrogate(text[^1]))text.Length--;
        return text.ToString();
    }
}
