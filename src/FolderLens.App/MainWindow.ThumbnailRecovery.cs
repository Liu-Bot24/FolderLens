using Microsoft.UI.Dispatching;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? thumbnailRetryTimer;
    private Func<TimeSpan>? verifyThumbnailRetryTime;
    private TimeSpan ThumbnailRetryNow=>verifyThumbnailRetryTime?.Invoke()??TimeSpan.FromMilliseconds(Environment.TickCount64);
    private void ScheduleThumbnailRecovery()
    {
        // Look only at realized, actually visible thumbnail consumers. Offscreen
        // rows keep their small budget with the normal row cache, not a retry queue.
        bool needed=!closing&&visibleContainers.Any(pair=>pair.Key.View!=FilesList
            &&pair.Value.Thumbnail is null&&pair.Value.ThumbnailRetryPending
            &&IsThumbnailInViewport(pair.Key.View,pair.Value));
        if(!needed){thumbnailRetryTimer?.Stop();return;}
        if(thumbnailRetryTimer is null)
        {
            thumbnailRetryTimer=DispatcherQueue.CreateTimer();thumbnailRetryTimer.Interval=TimeSpan.FromSeconds(1);thumbnailRetryTimer.IsRepeating=true;
            thumbnailRetryTimer.Tick+=(_,_)=>QueueThumbnailViewport();
        }
        if(!thumbnailRetryTimer.IsRunning)thumbnailRetryTimer.Start();
    }
}
