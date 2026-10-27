namespace FolderLens.Core;

public sealed class ScanPreviewRefresh
{
    public static bool DeferFirstBatch(int matchedFiles,bool scanning)=>scanning&&matchedFiles<32;
    public static bool SequenceLocked(bool hasResults,bool hasSelection,bool immersive,bool fullScreen,bool slideshow)
        =>hasResults&&(immersive||fullScreen||slideshow);
    private TimeSpan next;
    public void Reset()=>next=TimeSpan.Zero;
    /// <summary>Waits at least two seconds; a refresh that itself took long (a very large
    /// library) waits up to three times its cost, capped at eight seconds, so preview
    /// refreshes cannot keep the snapshot database busy for most of a scan.</summary>
    public void Complete(TimeSpan now,TimeSpan cost=default)
        =>next=now+TimeSpan.FromTicks(Math.Clamp(cost.Ticks*3,TimeSpan.FromSeconds(2).Ticks,TimeSpan.FromSeconds(8).Ticks));
    public bool TryBegin(long discoveredFiles,bool hasResults,bool queryBusy,bool sequenceLocked,TimeSpan now)
    {
        if(discoveredFiles==0||queryBusy||sequenceLocked||now<next)return false;
        next=now+TimeSpan.FromSeconds(hasResults?2:.25);
        return true;
    }
}
