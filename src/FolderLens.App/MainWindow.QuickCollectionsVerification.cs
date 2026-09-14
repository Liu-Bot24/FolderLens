using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyCollectionObservationRefresh(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        monitor?.Dispose();monitor=null;DetailsMode.IsChecked=false;ToggleView(DetailsMode,new());FilesGrid.UpdateLayout();
        await WaitUntil(()=>visible.Any(r=>r.Item is not null&&r.Thumbnail is not null),TimeSpan.FromSeconds(10));
        var row=visible.First(r=>r.Item is not null&&r.Thumbnail is not null);
        var image=row.Thumbnail;var itemsSource=FilesGrid.ItemsSource;var container=FilesGrid.ContainerFromItem(row);
        var old=row.Item!;string previousSession=resultHandle!.Id;
        await catalog!.Write(c=>{using var command=c.CreateCommand();command.CommandText="UPDATE Files SET path_revision=path_revision+2 WHERE entry_id=$id";command.Parameters.AddWithValue("$id",old.EntryId);return command.ExecuteNonQuery();});
        await RefreshQuery(scanPreview:true);
        var authority=(await catalog.ReadSnapshotEntries(resultHandle!.Id,[old.EntryId],lifetime.Token)).Single();
        report["snapshotChanged"]=resultHandle.Id!=previousSession;report["authoritativeRevision"]=authority.PathRevision;report["rowRevision"]=row.Item!.PathRevision;
        if(resultHandle.Id==previousSession||row.Item.PathRevision!=authority.PathRevision||row.Item.PathRevision!=old.PathRevision+2)throw new InvalidOperationException("新快照已经发布，但保留行仍使用旧路径修订。");
        if(!ReferenceEquals(itemsSource,FilesGrid.ItemsSource)||!ReferenceEquals(image,row.Thumbnail)||!ReferenceEquals(container,FilesGrid.ContainerFromItem(row))||!ReferenceEquals(row,results![checked((int)authority.Ordinal)]))throw new InvalidOperationException("更新观察值破坏了行、图片、容器或列表源的保留。");
        string tag=(await catalog.CreateCollection("刷新观察值验证")).Id;
        lastCollectionTargets=[tag];quickCollectionUsed=true;await QuickCollect(row);
        if(!row.IsCollected)throw new InvalidOperationException("新观察值快捷收藏失败。");
        if(await catalog.ChangeCollectionSelection([tag],resultHandle.Id,[new(authority.Ordinal,1)],true)!=0)throw new InvalidOperationException("普通与快捷收藏没有使用同一观察结果。");
        report["nativeRowThumbnailContainerAndSourceRetained"]=true;report["quickAndBulkAgree"]=true;report["status"]="PASS";
    }
    private async Task VerifyQuickCollections(string source,Dictionary<string,object> report)
    {
        await OpenRoot(Path.Combine(source,"A"));if(metadataTask is not null)await metadataTask;await RefreshQuery();
        DetailsMode.IsChecked=false;ToggleView(DetailsMode,new());FilesGrid.UpdateLayout();
        var first=(FileRow)results![0]!;var second=(FileRow)results[1]!;
        await results.EnsureLoaded(first,lifetime.Token);await results.EnsureLoaded(second,lifetime.Token);
        await ResolveRow(first,rootId,lifetime.Token);await ResolveRow(second,rootId,lifetime.Token);
        if(first.IsCollected||second.IsCollected)throw new InvalidOperationException("未收藏文件的初始星标不正确。");
        FilesGrid.SelectedItem=second;int dialogs=0;
        verifyCollectionDialog=(_,_)=>{dialogs++;return Task.CompletedTask;};
        await QuickCollect(first);
        if(dialogs!=1||quickCollectionUsed||lastCollectionTargets.Length!=0||first.IsCollected)throw new InvalidOperationException("取消首次快捷收藏后错误记住了目标。");
        verifyCollectionDialog=async(dialog,change)=>
        {
            dialogs++;var panel=(StackPanel)dialog.Content;
            ((TextBox)panel.Children[2]).Text="快捷收藏验证";
            if(!await change(true))throw new InvalidOperationException("首次快捷新建收藏失败。");
        };
        IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}
        }
        Button Star(FileRow row)=>Children((DependencyObject)FilesGrid.ContainerFromItem(row)).OfType<Button>().Single(b=>b.Content is FontIcon&&ReferenceEquals(b.DataContext,row));
        var star=Star(first);if(!star.IsEnabled)throw new InvalidOperationException("已加载缩略图的星标未启用。");
        ((IInvokeProvider)new ButtonAutomationPeer(star).GetPattern(PatternInterface.Invoke)).Invoke();
        for(int i=0;i<400&&(dialogs<2||quickCollectionBusy);i++)await Task.Delay(25);
        await ResolveRow(first,rootId,lifetime.Token);
        if(dialogs!=2||!quickCollectionUsed||!first.IsCollected||FilesGrid.SelectedItem!=second)throw new InvalidOperationException("星标按钮未完成首次收藏或改变了文件选择。");
        string id=lastCollectionTargets.Single();
        await QuickCollect(second);await QuickCollect(second);
        if(dialogs!=2||(await catalog!.ReadCollections()).Single(c=>c.Id==id).Count!=2)throw new InvalidOperationException("后续快捷收藏重复弹窗或重复存储。");
        await ResolveRow(second,rootId,lifetime.Token);
        FilesGrid.UpdateLayout();await Task.Delay(50);
        foreach(var row in new[]{first,second})
        {
            var button=Star(row);var icon=(FontIcon)button.Content;
            var parent=(FrameworkElement)VisualTreeHelper.GetParent(button);var point=button.TransformToVisual(parent).TransformPoint(new(0,0));
            if(icon.Glyph!="\uE735"||Math.Abs(parent.ActualWidth-point.X-button.ActualWidth-8)>2||Math.Abs(point.Y-8)>2)throw new InvalidOperationException("星标未在缩略图右上角或收藏状态没有更新。");
        }
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync((UIElement)FilesGrid.ContainerFromItem(first));
        using(var file=File.Create(Path.Combine(dataDirectory,"quick-collection-card.png")))using(var stream=file.AsRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        }
        // The ordinary menu path must still present its dialog on every call.
        verifyCollectionDialog=(_,_)=>{dialogs++;return Task.CompletedTask;};
        await CollectFiles();await CollectFiles();if(dialogs!=4)throw new InvalidOperationException("普通收藏菜单误用了快捷模式。");
        verifyCollectionDialog=async(dialog,change)=>
        {
            dialogs++;((TextBox)((StackPanel)dialog.Content).Children[2]).Text="菜单选择的新目标";
            if(!await change(true))throw new InvalidOperationException("普通菜单更换收藏目标失败。");
        };
        await CollectFiles();string nextId=lastCollectionTargets.Single();
        await QuickCollect(first);
        if(dialogs!=5||nextId==id||(await catalog.ReadCollections()).Single(c=>c.Id==nextId).Count!=2)throw new InvalidOperationException("快捷收藏未使用普通菜单最后成功选择的目标。");
        await catalog.DeleteCollection(id);RefreshCollectionBadges();await ResolveRow(first,rootId,lifetime.Token);
        if(!first.IsCollected)throw new InvalidOperationException("仍属于另一个收藏夹时星标被错误清除。");
        await catalog.DeleteCollection(nextId);RefreshCollectionBadges();await ResolveRow(first,rootId,lifetime.Token);
        if(first.IsCollected)throw new InvalidOperationException("删除最后一个收藏夹后星标未清除。");
        verifyCollectionDialog=(_,_)=>{dialogs++;return Task.CompletedTask;};
        await QuickCollect(first);if(dialogs!=6)throw new InvalidOperationException("上次收藏夹删除后没有重新选择。");
        verifyCollectionDialog=null;
        string staleTarget=(await catalog.CreateCollection("旧选择拒绝验证")).Id;
        lastCollectionTargets=[staleTarget];quickCollectionUsed=true;
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET entry_state='missing',file_version=file_version+1 WHERE entry_id=$id";cmd.Parameters.AddWithValue("$id",first.Item!.EntryId);return cmd.ExecuteNonQuery();});
        bool staleRejected=false;try{await QuickCollect(first);}catch(IOException){staleRejected=true;}
        if(!staleRejected||first.IsCollected||(await catalog.ReadCollections()).Single(c=>c.Id==staleTarget).Count!=0)throw new InvalidOperationException("失效缩略图仍显示快捷收藏成功。");
        await QuickCollect(second);
        if(!second.IsCollected||(await catalog.ReadCollections()).Single(c=>c.Id==staleTarget).Count!=1)throw new InvalidOperationException("拒绝失效选择后正常快捷收藏不可用。");
        report["staleQuickSelectionRejectedWithoutFalseStar"]=true;
        monitor?.Dispose();monitor=null;
        string? frozenSession=resultHandle?.Id;
        string original=Path.Combine(root,second.Item!.RelativePath);
        File.Move(original,original+".renamed");
        report["scanWasBusyBeforeRefreshVerification"]=scanTask is {IsCompleted:false};
        while(scanTask is {IsCompleted:false} activeScan)await activeScan.WaitAsync(TimeSpan.FromSeconds(10));
        reconcilePending=true;await Reconcile(force:true);
        if((await catalog.ReadCollections()).Single(c=>c.Id==staleTarget).Count!=0)throw new InvalidOperationException("收藏刷新验证的扫描尚未清除数据库归属。");
        if(fileCollections.Single(c=>c.Id==staleTarget).Count!=0)throw new InvalidOperationException("扫描清除归属后收藏树计数未自动刷新。");
        for(int i=0;i<100&&second.IsCollected;i++)await Task.Delay(20);
        if(second.IsCollected)throw new InvalidOperationException("冻结浏览期间扫描清除归属后可见星标未刷新。");
        if(resultHandle?.Id!=frozenSession)throw new InvalidOperationException("刷新收藏计数改变了冻结浏览序列。");
        report["scanRefreshesCollectionTreeAndStarWithoutReplacingSequence"]=true;
        // All programmatic focus/activation paths in verification must be inert.
        if(WindowFocus.IsForeground(this)||FocusIfForeground(Search,FocusState.Programmatic)||WindowFocus.MayActivate(this,WindowFocus.Foreground))throw new InvalidOperationException("后台验证可以夺取输入焦点。");
        report["firstCancelDoesNotRemember"]=true;report["nativeStarButtonAndTopRightLayout"]=true;
        report["repeatUsesLastCollectionWithoutDuplicates"]=true;report["ordinaryMenuAlwaysPresentsDialog"]=true;
        report["deletedTargetReprompts"]=true;report["backgroundFocusBlocked"]=true;
        report["ordinaryMenuUpdatesQuickTarget"]=true;report["multipleMembershipRetainsStar"]=true;
        report["foregroundDialogInteraction"]="NOT_RUN";report["doubaoVoiceSession"]="NOT_RUN";report["status"]="PASS";
    }
}
