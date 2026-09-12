namespace FolderLens.Core;

public sealed class ScanPreviewRefresh
{
    private TimeSpan next;
    public void Reset()=>next=TimeSpan.Zero;
    public void Complete(TimeSpan now)=>next=now+TimeSpan.FromSeconds(2);
    public bool TryBegin(long discoveredFiles,bool hasResults,bool queryBusy,bool sequenceLocked,TimeSpan now)
    {
        if(discoveredFiles==0||queryBusy||sequenceLocked||now<next)return false;
        next=now+TimeSpan.FromSeconds(hasResults?2:.25);
        return true;
    }
}
