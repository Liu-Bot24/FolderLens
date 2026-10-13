namespace FolderLens.Infrastructure;

// Per-file state only. The UI supplies monotonic time and owns one shared timer;
// this object never retains a source handle, task, bitmap or exception.
public sealed class ThumbnailRecovery(TimeSpan started)
{
    public static readonly TimeSpan Interval=TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Window=TimeSpan.FromMinutes(3);
    public const int MaximumRetries=36;
    private TimeSpan due;
    private int attempts;
    public bool Pending {get;private set;}
    public bool Expired(TimeSpan now)=>now-started>=Window||attempts>=MaximumRetries;
    public bool Failed(TimeSpan now)
    {
        Pending=!Expired(now);
        if(Pending)due=now+Interval;
        return Pending;
    }
    public bool TryBegin(TimeSpan now)
    {
        if(!Pending||Expired(now)||now<due)return false;
        Pending=false;attempts++;return true;
    }
    public static bool CanRetry(Exception error)
    {
        if(error is UnauthorizedAccessException or OperationCanceledException or CloudFileRequiresApprovalException or WorkerTemporaryAccessException)return false;
        if(error.Message is "FileChanged" or "AccessDenied" or "TextAccessDenied" or "CloudReadNotApproved" or "DeferredOffline" or "LinkSkipped" or "SourceExcluded" or "UnsupportedCloudProvider" or "MissingParentAuthorization")return false;
        if(error is TimeoutException)return true;
        if(error is InvalidDataException)return error.Message is "Timeout" or "SourceIoError";
        // Source probes and worker transports report temporary offline/missing
        // files as IO errors. Explicit disk-full failures cannot recover by reads.
        return error is IOException&&(error.HResult&0xffff) is not (39 or 112);
    }
}
