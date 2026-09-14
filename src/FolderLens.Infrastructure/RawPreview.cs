namespace FolderLens.Infrastructure;

public static class RawPreview
{
    public static async Task<T> Open<T>(Func<string,Task<T>> request)
    {
        try { return await request("rawEmbedded"); }
        catch(Exception error) when(MediaPreviewFallback.CanRetainRawPreview(error)) {}
        return await request("fit");
    }
}
