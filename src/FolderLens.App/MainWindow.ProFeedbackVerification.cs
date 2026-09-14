using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FolderLens.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyProFeedback(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        scanStop.Cancel();await LoadFormatChoices();
        if(!FormatOptions.IsEnabled||FormatOptions.Items.Count==0)throw new InvalidOperationException("取消扫描后不能读取已索引格式。");
        var row=(FileRow)results![0]!;await results.EnsureLoaded(row,lifetime.Token);visible.Add(row);
        string tag=(await catalog!.CreateCollection("取消后收藏验证")).Id;
        await catalog.ChangeCollectionMembers([tag],[row.Item!.EntryId],true);await LoadRowProperties(row,true);
        if(!row.IsCollected)throw new InvalidOperationException("取消扫描后收藏星标不能刷新。");
        report["cancelledScanKeepsIndexedBrowsing"]=true;
        string entry=row.Item.EntryId;
        includedCollectionIds=[tag];await RefreshQuery();if(results?.Count!=1)throw new InvalidOperationException("包含标签的初始结果不正确。");
        await DeleteCollectionAndRefresh(tag);if(results?.Count!=12||includedCollectionIds.Length!=0)throw new InvalidOperationException("删除包含标签后没有立即刷新结果。");
        tag=(await catalog.CreateCollection("排除验证")).Id;await catalog.ChangeCollectionMembers([tag],[entry],true);
        excludedCollectionIds=[tag];await RefreshQuery();if(results?.Count!=11)throw new InvalidOperationException("排除标签的初始结果不正确。");
        await DeleteCollectionAndRefresh(tag);if(results?.Count!=12||excludedCollectionIds.Length!=0)throw new InvalidOperationException("删除排除标签后没有恢复结果。");
        tag=(await catalog.CreateCollection("容量范围验证")).Id;await catalog.ChangeCollectionMembers([tag],[entry],true);
        await RefreshCollectionsTree();await OpenCollection(tag);UpdateCommandAvailability();
        if(CapacityButton.IsEnabled)throw new InvalidOperationException("收藏虚拟根仍允许打开物理目录容量。");
        report["deletedTagRefreshesBothDirections"]=true;report["virtualCollectionCapacityGuard"]=true;
        report["status"]="PASS";
    }
    private async Task VerifyCollectionMarkdown(string source,Dictionary<string,object> report)
    {
        string document=Path.Combine(source,"note.md");
        await File.WriteAllTextAsync(document,"# 收藏中的文档\n\n![local](A/image-00.png)\n\n![blocked](https://example.invalid/image.png)");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        ApplySavedFilter(new FilterSpec{RootId=rootId,Kinds=[]});await RefreshQuery();
        var items=await catalog!.ReadPage(resultHandle!.Id,0);var item=items.Single(i=>i.Kind=="markdown");
        string tag=(await catalog.CreateCollection("Markdown 来源验证")).Id;await catalog.ChangeCollectionMembers([tag],[item.EntryId],true);
        await RefreshCollectionsTree();await OpenCollection(tag);
        var row=(FileRow)results![0]!;await results.EnsureLoaded(row,lifetime.Token);await SelectPreview(row);
        if(MarkdownHost.Visibility!=Visibility.Visible||markdownImages.Count!=1)throw new InvalidOperationException("收藏 Markdown 没有使用物理源根加载本地内嵌图片："+QualityLabel.Text);
        if(WindowFocus.IsForeground(this))throw new InvalidOperationException("Markdown 后台验证抢占前台。");
        report["crossRootMarkdownLocalImage"]=true;report["remoteImageNotLoaded"]=true;report["status"]="PASS";
    }
    private async Task VerifyCollectionPendingClose(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);
        // Real menu operation, no ShowAsync seam: it waits for our window to own
        // foreground before opening, while shutdown must cancel and retire it.
        Task pending=CollectFiles();await Task.Delay(100);
        if(pending.IsCompleted)throw new InvalidOperationException("收藏对话框等待没有启动。");
        verifyClosingState=async()=>
        {
            bool completed=pending.IsCompletedSuccessfully;
            await File.WriteAllTextAsync(Path.Combine(dataDirectory,"native-close.json"),System.Text.Json.JsonSerializer.Serialize(new{status=completed?"PASS":"FAIL",collectionWaitRetired=completed,foregroundPopup="NOT_RUN"}));
            if(!completed)Environment.ExitCode=1;
        };
        report["status"]="CLOSE_PENDING";
    }
}
