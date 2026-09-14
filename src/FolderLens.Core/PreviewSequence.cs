namespace FolderLens.Core;

public static class PreviewSequence
{
    public static bool IsMedia(string kind)=>kind is "image" or "video";
    public static int Move(IReadOnlyList<string> kinds,int origin,int steps)
    {
        int target=origin,direction=Math.Sign(steps);long remaining=Math.Abs((long)steps);
        if(direction==0)return target;
        for(int index=origin+direction;index>=0&&index<kinds.Count;index+=direction)
            if(IsMedia(kinds[index])){target=index;if(--remaining==0)break;}
        return target;
    }
}
