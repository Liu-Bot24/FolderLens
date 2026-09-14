namespace FolderLens.Core;

public static class SlideshowSequence
{
    public static async Task<int?> Next(int count,int current,Func<int,CancellationToken,Task<bool>> isImage,CancellationToken token)
    {
        for(int index=current+1;index<count;index++)
        {
            token.ThrowIfCancellationRequested();
            bool image=await isImage(index,token);
            token.ThrowIfCancellationRequested();
            if(image)return index;
        }
        return null;
    }
}
