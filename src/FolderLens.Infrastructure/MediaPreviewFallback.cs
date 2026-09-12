namespace FolderLens.Infrastructure;

public static class MediaPreviewFallback
{
    // A preview is retained only for a decode/capacity failure. Stale input,
    // cancellation, permission and protocol errors must reach their own handlers.
    public static bool CanRetainRawPreview(Exception error)=>
        error is TimeoutException or OutOfMemoryException or WorkerResourceLimitException ||
        error is InvalidDataException && error.Message is
            "DecodeFailed" or "UnsupportedCodec" or "Timeout" or "ResourceLimit" or "OutOfMemory";
}
