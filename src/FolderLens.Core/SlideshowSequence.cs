namespace FolderLens.Core;

public static class SlideshowSequence
{
    public static async Task<int?> Next(int count,int current,Func<int,CancellationToken,Task<bool>> isImage,CancellationToken token,bool repeat=false)
    {
        for(int index=current+1;index<count;index++)
        {
            token.ThrowIfCancellationRequested();
            bool image=await isImage(index,token);
            token.ThrowIfCancellationRequested();
            if(image)return index;
        }
        if(repeat)
            for(int index=0;index<=current&&index<count;index++){token.ThrowIfCancellationRequested();if(await isImage(index,token)){token.ThrowIfCancellationRequested();return index;}}
        return null;
    }
}
