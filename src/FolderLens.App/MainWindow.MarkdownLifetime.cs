using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim markdownLoadGate=new(1,1);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? markdownReleaseTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? markdownViewportTimer;
    private bool markdownLoading;
    private void InitializeMarkdownLifetime()
    {
        markdownViewportTimer=DispatcherQueue.CreateTimer();markdownViewportTimer.Interval=TimeSpan.FromMilliseconds(200);
        markdownViewportTimer.Tick+=async(_,_)=>await UpdateMarkdownViewport();
        markdownReleaseTimer=DispatcherQueue.CreateTimer();markdownReleaseTimer.Interval=TimeSpan.FromSeconds(60);markdownReleaseTimer.IsRepeating=false;
        markdownReleaseTimer.Tick+=(_,_)=>
        {
            if(!closing&&!markdownLoading&&MarkdownHost.Visibility!=Visibility.Visible)ReleaseMarkdownView();
        };
        MarkdownHost.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,(_,_)=>{ScheduleMarkdownRelease();if(MarkdownHost.Visibility!=Visibility.Visible)_=PauseMarkdownMedia();});
    }
    private void ScheduleMarkdownRelease()
    {
        if(markdownReleaseTimer is null)return;
        if(!closing&&MarkdownHost.Visibility==Visibility.Visible)markdownViewportTimer?.Start();else markdownViewportTimer?.Stop();
        if(closing||markdownLoading||markdown is null||MarkdownHost.Visibility==Visibility.Visible)markdownReleaseTimer.Stop();
        else if(!markdownReleaseTimer.IsRunning)markdownReleaseTimer.Start();
    }
    private void ReleaseMarkdownView()
    {
        markdownReleaseTimer?.Stop();markdownViewportTimer?.Stop();var previous=markdown;markdown=null;
        navigationComplete?.TrySetCanceled();navigationComplete=null;markdownDocument=[];ResetMarkdownResources();markdownDocumentUrl="";
        MarkdownHost.Content=null;previous?.Close();
        if(previous is not null)RecordWebView("Markdown view released");
    }
    private async Task DisposeMarkdownView()
    {
        markdownReleaseTimer?.Stop();await markdownLoadGate.WaitAsync();
        try{ReleaseMarkdownView();}finally{markdownLoadGate.Release();}
    }
}
