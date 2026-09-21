using FolderLens.Infrastructure;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private WorkerClient? textThumbnailWorker;
    private readonly SemaphoreSlim textThumbnailSlot=new(1,1);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? textThumbnailResizeTimer;
    private Action<FileRow,int>? verifyTextExcerptRead;
    private readonly LinkedList<FileRow> textExcerptRows=new();
    private void RememberTextExcerpt(FileRow row)
    {
        textExcerptRows.Remove(row);textExcerptRows.AddLast(row);TrimTextExcerpts();
    }
    private void TrimTextExcerpts()
    {
        // WinUI can retain recycled row identities independently of page eviction.
        // Retain only 128 offscreen excerpts, plus the currently realized cards.
        int excess=textExcerptRows.Count(row=>!visible.Contains(row))-128;
        for(var node=textExcerptRows.First;node is not null&&excess>0;)
        {
            var next=node.Next;
            if(!visible.Contains(node.Value)){node.Value.SetTextExcerpt("",0,false);textExcerptRows.Remove(node);excess--;}
            node=next;
        }
    }
    private void ClearTextExcerpts()
    {
        foreach(var row in textExcerptRows)row.SetTextExcerpt("",0,false);
        textExcerptRows.Clear();
    }
    private void ScheduleTextThumbnails()
    {
        if(closing)return;
        if(textThumbnailResizeTimer is null)
        {
            textThumbnailResizeTimer=DispatcherQueue.CreateTimer();
            textThumbnailResizeTimer.IsRepeating=false;textThumbnailResizeTimer.Interval=TimeSpan.FromMilliseconds(180);
            textThumbnailResizeTimer.Tick+=(_,_)=>
            {
                if(closing)return;
                foreach(var (container,row) in visibleContainers.ToArray())
                    if(container.View!=FilesList&&row.NeedsTextExcerpt)_=LoadThumbnail(container.View,row);
            };
        }
        textThumbnailResizeTimer.Stop();textThumbnailResizeTimer.Start();
    }
}
