using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyScrollOffsetAndFolderHeader(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Directory.EnumerateFiles(source,"*.png",SearchOption.AllDirectories).First());
        source=Path.Combine(source,"scroll-offset");
        foreach(string directory in new[]{"A","B"})
        {
            Directory.CreateDirectory(Path.Combine(source,directory));
            for(int i=0;i<64;i++)await File.WriteAllBytesAsync(Path.Combine(source,directory,$"offset-{i:D3}.png"),sample);
        }
        folderGrouping=new(true,"first","name","asc","all");
        ChooseBrowserView(ThumbnailMode,new RoutedEventArgs());
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(queryBusy)await queryCompletion;
        await RefreshQuery(preserveViewport:true);Shell.UpdateLayout();UpdateCurrentFolderHeader();
        var scroll=FindScrollViewer(FilesGrid)??throw new InvalidOperationException("缺少滚动视图。");
        await WaitUntil(()=>Math.Abs(scroll.VerticalOffset)<1,TimeSpan.FromSeconds(3));
        var groups=browserGroups??throw new InvalidOperationException("缺少文件夹分组。");
        var first=groups.Single(group=>group.Info.RelativePath=="A");
        var second=groups.Single(group=>group.Info.RelativePath=="B");
        await WaitUntil(()=>ReferenceEquals(CurrentFolderHeader.DataContext,first),TimeSpan.FromSeconds(3));
        double headerBottom=CurrentFolderHeader.TransformToVisual(BrowserPane).TransformPoint(new(0,0)).Y+CurrentFolderHeader.ActualHeight;
        double cardsTop=FilesGrid.TransformToVisual(BrowserPane).TransformPoint(new(0,0)).Y;
        if(CurrentFolderHeader.Visibility!=Visibility.Visible||headerBottom>cardsTop+.5)throw new InvalidOperationException("固定目录栏遮挡了图片区域。");
        report["fixedHeaderDoesNotOverlayCards"]=true;

        AcceptBrowserInteraction(changesViewport:true);
        FilesGrid.ScrollIntoView(results![checked((int)second.Info.Start)],ScrollIntoViewAlignment.Leading);
        await WaitUntil(()=>ReferenceEquals(CurrentFolderHeader.DataContext,second)&&!browserMovementPending,TimeSpan.FromSeconds(4));
        if(CurrentFolderSummary.Text!=second.Summary||!CurrentFolderTitle.Text.Contains(second.Title))throw new InvalidOperationException("跨目录后固定栏内容不匹配。");
        double middle=scroll.VerticalOffset;RestorePublicationViewport(FilesGrid,middle);
        // A late native anchoring pass after publication must not win over the
        // numeric position. This is not a simulated user gesture.
        scroll.ChangeView(null,Math.Min(scroll.ScrollableHeight,middle+80),null,true);
        await WaitUntil(()=>Math.Abs(scroll.VerticalOffset-middle)<1,TimeSpan.FromSeconds(3));
        report["lateNativeAnchoringCorrected"]=true;

        AcceptBrowserInteraction(changesViewport:true);
        double requested=Math.Min(scroll.ScrollableHeight,middle+120);scroll.ChangeView(null,requested,null,true);
        await WaitUntil(()=>!browserMovementPending,TimeSpan.FromSeconds(3));
        QueueBrowserOffset();await Task.Delay(80);
        if(Math.Abs(scroll.VerticalOffset-requested)>1)throw new InvalidOperationException("旧纠正请求抢回了用户滚动。");
        report["newUserScrollWins"]=true;

        RestorePublicationViewport(FilesGrid,scroll.VerticalOffset);
        AcceptBrowserInteraction(changesViewport:true);
        ToggleFolderGroup(new Button{DataContext=first},new RoutedEventArgs());
        if(browserMovementPending||browserOffsetOwner is null)throw new InvalidOperationException("滚动尚未静止时，显式折叠未接管定位。");
        await Task.Delay(100);Shell.UpdateLayout();
        if(browserOffsetOwner is {} owner&&owner.Offset>scroll.ScrollableHeight+1)throw new InvalidOperationException("折叠后仍保留不可达的旧偏移。");
        report["collapseCommitsReachableOffset"]=true;
        report["collapseDuringScrollSettlingOwnsOffset"]=true;
        report["headerFollowsCurrentDirectory"]=true;
        report["status"]="PASS";
    }
}
