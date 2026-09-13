using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyRootFromViewer(string directory,string target,Dictionary<string,object> report)
    {
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);await SetImmersive(true);
        if(!immersive)throw new InvalidOperationException("未进入沉浸查看。");
        await OpenRoot(target);if(metadataTask is not null)await metadataTask;
        report["resultCount"]=resultHandle?.Count??-1;report["viewerStillOpen"]=immersive;report["rootMatches"]=root==target;
        if(immersive||root!=target||resultHandle?.Count!=12)throw new InvalidOperationException("从沉浸看图打开含图目录后，没有恢复浏览结果。");
        report["status"]="PASS";
    }
    private async Task VerifySlideTickRace(string directory,Dictionary<string,object> report)
    {
        var failures=new List<string>();var checks=new List<object>();
        foreach(string action in new[]{"advance","pause","selection","restart-error","restart-wait"})
        {
            report["currentAction"]=action;report["phase"]="open";
            await OpenRoot(directory);if(metadataTask is not null)await metadataTask;
            var original=results??throw new InvalidOperationException($"没有浏览结果：busy={queryBusy}, status={Status.Text}, summary={ResultSummary.Text}");
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            report["phase"]="select";
            var first=(FileRow)original[0]!;await original.EnsureLoaded(first,lifetime.Token);await SelectPreview(first);
            var later=(FileRow)original[2]!;await original.EnsureLoaded(later,lifetime.Token);
            int attempts=0;
            verifySlideAdvanceBarrier=async token=>{if(++attempts>1&&action=="restart-wait")return;entered.TrySetResult();await release.Task;if(action=="restart-error")throw new IOException("Obsolete slideshow tick");};
            var interval=slideTimer!.Interval;verifySlideTickCompleted=()=>finished.TrySetResult();
            try
            {
                slideShow=true;slideTimer.Interval=TimeSpan.FromMilliseconds(1);slideTimer.Start();
                report["phase"]="tick-enter";
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                report["phase"]="user-action";
                if(action=="selection")await SelectPreview(later);
                else if(action!="advance")
                {
                    await ToggleSlideshowCore();
                    if(action.StartsWith("restart")){await SelectPreview(later);await ToggleSlideshowCore();if(action=="restart-error")slideTimer.Stop();}
                }
                bool newTimerContinued=true;
                report["phase"]="new-tick";
                if(action=="restart-wait"){try{await WaitUntil(()=>attempts>1,TimeSpan.FromMilliseconds(300));}catch(TimeoutException){newTimerContinued=false;}}
                slideTimer.Stop();slideTimer.Interval=interval;var expected=selected;bool expectedPlaying=slideShow;
                release.TrySetResult();await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                report["phase"]="check";
                await Task.Delay(50);
                bool retained=action=="advance"?selected?.Ordinal==1:ReferenceEquals(expected,selected),playing=slideShow==expectedPlaying;
                checks.Add(new{action,retained,playing,newTimerContinued,selectedOrdinal=selected?.Ordinal});if(!retained||!playing||!newTimerContinued)failures.Add(action);
            }
            finally{release.TrySetResult();slideTimer.Stop();slideTimer.Interval=interval;slideShow=false;verifySlideTickCompleted=null;verifySlideAdvanceBarrier=null;selected=null;}
        }
        report["checks"]=checks;report["status"]=failures.Count==0?"PASS":"FAIL";
        if(failures.Count>0)throw new InvalidOperationException("过期幻灯片定时任务覆盖当前浏览状态："+string.Join(",",failures));
    }
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class VerificationFileGroup(System.Collections.IList items)
    {
        public System.Collections.IList Items=>items;
        public string Title=>"Fixture";
        public string Summary=>"";
    }
    private async Task VerifyGroupLaziness(Dictionary<string,object> report)
    {
        void Stage(string value)=>File.AppendAllText(Path.Combine(InitialDataDirectory!,"group-stages.log"),value+Environment.NewLine);
        var samples=new List<object>();
        foreach(string mode in new[]{"unbound","view","grid"})
        {
            Stage(mode+" start");
            FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;
            using var old=new VirtualResults(10000,(_,_)=>throw new InvalidOperationException("No file reads expected."));
            using var next=new VirtualResults(47329,(_,_)=>throw new InvalidOperationException("No file reads expected."));
            var first=new BrowserFileGroup(old,new("a","A",0,10000,0,10000,"ready","all"));
            var second=new BrowserFileGroup(next,new("b","B",0,37329,10000,37329,"ready","all"));
            var groups=new System.Collections.ObjectModel.ObservableCollection<BrowserFileGroup>{first};
            using var view=mode=="unbound"?null:new BrowserCollectionView(groups);
            if(mode=="grid")FilesGrid.ItemsSource=view;
            Stage(mode+" attached");
            Shell.UpdateLayout();await Task.Delay(50);
            Stage(mode+" initial layout");
            long allocated=GC.GetAllocatedBytesForCurrentThread();var timer=System.Diagnostics.Stopwatch.StartNew();
            groups.Add(second);timer.Stop();long allocation=GC.GetAllocatedBytesForCurrentThread()-allocated;
            Stage(mode+" added");
            int synchronousRows=next.CachedRows().Count();
            Shell.UpdateLayout();await Task.Delay(50);int rows=next.CachedRows().Count();
            Stage(mode+" final layout");
            samples.Add(new{mode,milliseconds=timer.Elapsed.TotalMilliseconds,allocatedBytes=allocation,synchronousRows,rows});
            report["groupLazy"]=samples;
            if(rows>1024)throw new InvalidOperationException($"生产分组视图提前实例化 {rows} 个屏幕外文件。");
            FilesGrid.ItemsSource=null;
        }
        report["status"]="PASS";
    }    private async Task VerifyTreeSelectionVisible(string directory,byte[] png,Dictionary<string,object> report)
    {
        for(int i=0;i<40;i++)Directory.CreateDirectory(Path.Combine(directory,$"a-{i:D2}"));
        string target=Path.Combine(directory,"z-selected");Directory.CreateDirectory(target);await File.WriteAllBytesAsync(Path.Combine(target,"preview.png"),png);
        await OpenRoot(target);if(physicalTreeTask is not null)await physicalTreeTask;Shell.UpdateLayout();await Task.Delay(100);
        var node=FolderTree.SelectedNode;if(node is null||node!=activeTreeRoot)throw new InvalidOperationException("目录树没有选中当前根。");
        var container=FolderTree.ContainerFromNode(node) as FrameworkElement;
        report["containerRealized"]=container is not null;
        if(container is null)throw new InvalidOperationException("当前目录仍在视口外，未实现选中容器。");
        var bounds=container.TransformToVisual(FolderTree).TransformBounds(new(0,0,container.ActualWidth,container.ActualHeight));
        report["top"]=bounds.Top;report["bottom"]=bounds.Bottom;report["viewportHeight"]=FolderTree.ActualHeight;
        if(bounds.Top<0||bounds.Bottom>FolderTree.ActualHeight+1)throw new InvalidOperationException("当前目录未滚入目录树视口。");
        var laterSelection=FolderTree.RootNodes[0];FolderTree.SelectedNode=laterSelection;
        FindTreeList(FolderTree)?.ScrollIntoView(laterSelection,ScrollIntoViewAlignment.Leading);Shell.UpdateLayout();await Task.Delay(50);
        double offset=FindScrollViewer(FolderTree)?.VerticalOffset??0;
        await RefreshAncestors(node,rootChangeVersion,lifetime.Token);Shell.UpdateLayout();await Task.Delay(50);
        if(FolderTree.SelectedNode!=laterSelection||Math.Abs((FindScrollViewer(FolderTree)?.VerticalOffset??0)-offset)>1)throw new InvalidOperationException("旧目录刷新夺回了后来的目录选择或滚动位置。");
        report["laterSelectionRetained"]=true;
        report["status"]="PASS";
    }
    private async Task VerifyPreviewLayout(Dictionary<string,object> report)
    {
        report["titleFont"]=FileTitle.FontFamily.Source;report["titleLanguage"]=FileTitle.Language;
        double original=PreviewPane.Width;var originalSelection=selected;var errors=new List<string>();
        try
        {
            var padding=PreviewPane.Padding;var border=PreviewPane.BorderThickness;
            SetFullScreenChrome(true);SetFullScreenChrome(true);
            if(PreviewPane.Padding!=new Thickness(0)||PreviewPane.BorderThickness!=new Thickness(0)||PreviewActions.Visibility!=Visibility.Collapsed)errors.Add("全屏仍显示窗口边距或按钮");
            SetFullScreenChrome(false);SetFullScreenChrome(false);
            if(PreviewPane.Padding!=padding||PreviewPane.BorderThickness!=border||PreviewActions.Visibility!=Visibility.Visible)errors.Add("退出全屏没有恢复预览布局");
            foreach(string kind in new[]{"image","video"})
            {
            selected=new FileRow(0);selected.Fill(new(0,"layout",1,kind=="image"?"图片预览.png":"视频预览.mp4","",0,null,kind));UpdateViewerInformation();
            if(PreviewFilePath.Text!=selected.RelativePath||PreviewFilePath.Visibility!=Visibility.Visible)errors.Add("预览底部未显示选中文件的相对路径");
            foreach(int width in new[]{220,300,650})
            {
                PreviewPane.Width=width;Shell.UpdateLayout();await Task.Delay(30);
                if(PreviewFilePath.FontSize!=FileTitle.FontSize||PreviewFilePath.ActualWidth>PreviewPane.ActualWidth)errors.Add("路径字号或窄栏布局不正确");
                foreach(var button in PreviewActions.Children.OfType<Button>().Where(button=>button.Visibility==Visibility.Visible))
                {
                    var bounds=button.TransformToVisual(PreviewPane).TransformBounds(new(0,0,button.ActualWidth,button.ActualHeight));
                    if(bounds.X<0||bounds.Right>width+1||bounds.Width<28||bounds.Height<28)errors.Add($"{width}: 预览按钮越界或过小 ({bounds.X},{bounds.Width})");
                    if(ToolTipService.GetToolTip(button) is null)errors.Add($"{width}: 预览按钮缺少说明");
                }
                var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(PreviewPane);
                using var memory=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,memory);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
                memory.Seek(0);using var input=memory.AsStreamForRead();using var output=File.Create(Path.Combine(dataDirectory,$"preview-{kind}-{width}.png"));await input.CopyToAsync(output);
            }
            }
        }
        finally{PreviewPane.Width=original;selected=originalSelection;UpdateViewerInformation();}
        report["errors"]=errors;if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));report["status"]="PASS";
    }
    private void VerifyIdentityScale(Dictionary<string,object> report)
    {
        var samples=new List<object>();
        foreach(int count in new[]{1000,8192,8192})
        {
            using var previous=new VirtualResults(count,(_,_)=>throw new InvalidOperationException("Identity transfer must not read files."));
            using var next=new VirtualResults(count+1,(_,_)=>throw new InvalidOperationException("Identity transfer must not read files."));
            var held=Enumerable.Range(0,count).Select(index=>(FileRow)previous[index]!).ToArray();
            for(int i=0;i<Math.Min(count,4096);i++)next.Retain(held[i],i+1,null);
            long allocated=GC.GetAllocatedBytesForCurrentThread();int collections=GC.CollectionCount(0);var timer=System.Diagnostics.Stopwatch.StartNew();
            using(previous.ShareUnchangedRowsWith(next,[new(0,1,count,null)])){}
            timer.Stop();long allocation=GC.GetAllocatedBytesForCurrentThread()-allocated;
            samples.Add(new{count,milliseconds=timer.Elapsed.TotalMilliseconds,allocatedBytes=allocation,gen0=GC.CollectionCount(0)-collections});
            report["identityScale"]=samples;
            if(allocation>32L<<20)throw new InvalidOperationException("重复身份迁移发生异常的大量分配。");
            previous.Dispose();
            for(int i=0;i<count;i++)if(!ReferenceEquals(held[i],next[i+1])||next.IndexOf(held[i])!=i+1)throw new InvalidOperationException("迁移遗漏仍被持有的行。");
            if(next.IndexOf(new FileRow(1))!=-1)throw new InvalidOperationException("错误接纳外来源行。");
        }
        report["identityScale"]=samples;report["status"]="PASS";
    }
    private async Task VerifyThumbnailBounds(string directory,Dictionary<string,object> report)
    {
        int active=0,peak=0,capacity=WorkerResources.Shared.ThumbnailConcurrency*2;
        var fixture=Directory.EnumerateFiles(directory,"*.png",SearchOption.AllDirectories).First();
        for(int i=0;i<capacity;i++)File.Copy(fixture,Path.Combine(directory,$"pipeline-{i:D2}.png"));
        verifyMetadataBarrier=async token=>{active++;peak=Math.Max(peak,active);try{await Task.Delay(Timeout.Infinite,token);}finally{active--;}};
        try
        {
            await OpenRoot(directory);await WaitUntil(()=>active==capacity,TimeSpan.FromSeconds(5));
            if(peak>capacity||thumbnailSlots.CurrentCount!=WorkerResources.Shared.ThumbnailConcurrency||thumbnailPool.Count!=WorkerResources.Shared.ThumbnailConcurrency)throw new InvalidOperationException("元数据阻塞时解码器未归还或在途队列越界。");
            CancelThumbnails();await WaitUntil(()=>thumbnailWorkCount==0,TimeSpan.FromSeconds(5));
            if(active!=0||thumbnailPipelines.CurrentCount!=capacity||thumbnailSlots.CurrentCount!=WorkerResources.Shared.ThumbnailConcurrency)throw new InvalidOperationException("取消后在途名额未归还。");
            report["peakMetadataTails"]=peak;report["status"]="PASS";
        }
        finally{verifyMetadataBarrier=null;CancelThumbnails();}
    }
    private async Task VerifyCacheFallback(string directory,Dictionary<string,object> report)
    {
        await thumbnailCache!.DisposeAsync();thumbnailCache=new(Path.Combine(dataDirectory,"tiny-cache"),new(){MaxEntryBytes=24});await thumbnailCache.Initialize();
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await WaitUntil(()=>visible.Count>0&&visible.All(row=>row.Thumbnail is not null)&&thumbnailWorkCount==0,TimeSpan.FromSeconds(10));
        var errors=visible.Where(row=>row.ThumbnailError.Length>0).Select(row=>row.ThumbnailError).ToArray();report["errors"]=errors;
        if(errors.Length>0||thumbnailSlots.CurrentCount!=WorkerResources.Shared.ThumbnailConcurrency||thumbnailPipelines.CurrentCount!=WorkerResources.Shared.ThumbnailConcurrency*2)throw new InvalidOperationException("缓存不可用时图片报错或名额未归还。");
        report["status"]="PASS";
    }
    private async Task VerifySlideError(string directory,Dictionary<string,object> report)
    {
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var baseline=results!;var items=await catalog!.ReadPage(resultHandle!.Id,0,2);ClearResultSelection();selected=null;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source=new VirtualResults(2,async(_,token)=>{entered.TrySetResult();await release.Task.WaitAsync(token);throw new IOException("Controlled old read failure");});
        var b=(FileRow)source[1]!;b.Fill(items[1]);results=source;BindBrowserResults(source,[]);
        slideShow=false;ToggleSlideshow(this,new RoutedEventArgs());await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ToggleSlideshow(this,new RoutedEventArgs());await SelectBrowserOrdinal(source,1,lifetime.Token);
        var newDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);verifySlideEventCompleted=()=>newDone.TrySetResult();ToggleSlideshow(this,new RoutedEventArgs());await newDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var oldDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);verifySlideEventCompleted=()=>oldDone.TrySetResult();string currentStatus=Status.Text;
        try{release.TrySetResult();await oldDone.Task.WaitAsync(TimeSpan.FromSeconds(3));if(!slideShow||Status.Text!=currentStatus)throw new InvalidOperationException("旧启动异常覆盖了新启动状态。");report["status"]="PASS";}
        finally{verifySlideEventCompleted=null;slideShow=false;slideTimer?.Stop();results=baseline;BindBrowserResults(baseline,[]);await SetImmersive(false);}
    }
    private async Task VerifySelectionRace(string directory,Dictionary<string,object> report)
    {
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var baseline=results!;var items=await catalog!.ReadPage(resultHandle!.Id,0,2);var errors=new List<string>();
        foreach(bool details in new[]{true,false})foreach(string action in new[]{"user","program","switch","pause"})
        {
            ClearResultSelection();selected=null;
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source=new VirtualResults(2,async(_,token)=>{entered.TrySetResult();await release.Task.WaitAsync(token);return items;});
            var b=(FileRow)source[1]!;b.Fill(items[1]);results=source;
            DetailsMode.IsChecked=details;ToggleView(DetailsMode,new RoutedEventArgs());BindBrowserResults(source,[]);
            slideShow=false;
            Task pending=action=="pause"?ToggleSlideshowCore():SelectBrowserOrdinal(source,0,lifetime.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                if(action=="user"){ActiveView().SelectedIndex=1;await WaitUntil(()=>ReferenceEquals(selected,b),TimeSpan.FromSeconds(2));}
                else if(action=="program")await SelectBrowserOrdinal(source,1,lifetime.Token);
                else if(action=="pause")await ToggleSlideshowCore();
                else{DetailsMode.IsChecked=!details;ToggleView(DetailsMode,new RoutedEventArgs());}
            }
            finally{release.TrySetResult();await pending;}
            int wanted=action=="pause"?-1:action=="switch"?0:1;
            if(ActiveView().SelectedIndex!=wanted||(wanted<0?selected is not null||slideShow:!ReferenceEquals(selected,source[wanted])))errors.Add($"{details}/{action}:旧请求覆盖了较新的选择或视图切换丢失请求");
            results=baseline;BindBrowserResults(baseline,[]);
        }
        report["errors"]=errors;if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));report["status"]="PASS";
        ListViewBase ActiveView()=>DetailsMode.IsChecked==true?FilesList:FilesGrid;
    }
    private async Task VerifyActiveViewEntry(string directory,Dictionary<string,object> report)
    {
        var errors=new List<string>();
        foreach(bool details in new[]{true,false})
        {
            DetailsMode.IsChecked=details;ToggleView(DetailsMode,new RoutedEventArgs());
            try
            {
                await OpenPath(Path.Combine(directory,"A","image-03.png"));
                await WaitUntil(()=>selected?.Name=="image-03.png"&&selected.Item is not null,TimeSpan.FromSeconds(2));
                if((details?(ListViewBase)FilesList:FilesGrid).SelectedItem!=selected||(details?(ListViewBase)FilesGrid:FilesList).ItemsSource is not null)errors.Add($"{details}:打开文件未选择活动视图");
            }
            catch(Exception error){errors.Add($"{details}:OpenPath {error.GetType().Name}");}
            if(metadataTask is not null)await metadataTask;
            await RefreshQuery();ClearResultSelection();selected=null;slideShow=false;
            try{await ToggleSlideshowCore();if(!slideShow||selected?.Kind!="image"||(details?(ListViewBase)FilesList:FilesGrid).SelectedIndex!=0)errors.Add($"{details}:首图幻灯片未启动");}
            catch(Exception error){errors.Add($"{details}:Slideshow {error.GetType().Name}");}
            slideShow=false;slideTimer?.Stop();await SetImmersive(false);
        }
        report["errors"]=errors;if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));report["status"]="PASS";
    }
    private async Task VerifyToolbarWidths(Dictionary<string,object> report)
    {
        var errors=new List<string>();double original=BrowserPane.Width;
        try
        {
            foreach(int width in new[]{240,360,520,1000,360,1000})
            {
                BrowserPane.Width=width;Shell.UpdateLayout();UpdateBrowserToolbar();Shell.UpdateLayout();await Task.Delay(50);
                var bounds=new List<(FrameworkElement Element,Windows.Foundation.Rect Bounds)>();
                foreach(var control in new FrameworkElement[]{Category,SortField,Descending,GroupingButton,Search,QuickFiltersButton,DetailsMode,LargePreviewButton})
                {
                    var box=control.TransformToVisual(BrowserPane).TransformBounds(new(0,0,control.ActualWidth,control.ActualHeight));bounds.Add((control,box));
                    if(box.X<0||box.Right>width+1||box.Width<=0)errors.Add($"{width}: {control.Name}超出可用宽度");
                }
                for(int i=0;i<bounds.Count;i++)for(int j=i+1;j<bounds.Count;j++)
                {var a=bounds[i].Bounds;var b=bounds[j].Bounds;if(Math.Min(a.Right,b.Right)-Math.Max(a.Left,b.Left)>1&&Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Top,b.Top)>1)errors.Add($"{width}: 控件重叠");}
                var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(BrowserToolbar);
                using var memory=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,memory);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
                memory.Seek(0);using var input=memory.AsStreamForRead();using var output=File.Create(Path.Combine(dataDirectory,$"toolbar-{width}.png"));await input.CopyToAsync(output);
            }
        }
        finally{BrowserPane.Width=original;}
        report["errors"]=errors;if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));report["status"]="PASS";
    }
    private async Task VerifyAuditThree(string directory,Dictionary<string,object> report)
    {
        await OpenRoot(directory);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var errors=new List<string>();
        // Switching views transfers selection and leaves exactly one subscribed view.
        syncingBrowserSelection=true;
        FilesGrid.SelectRange(new ItemIndexRange(2,3));syncingBrowserSelection=false;
        var ranges=SelectedOrdinals(FilesGrid).ToArray();
        DetailsMode.IsChecked=true;ToggleView(DetailsMode,new RoutedEventArgs());await Task.Delay(100);
        if(FilesGrid.ItemsSource is not null||FilesList.ItemsSource is null||!ranges.SequenceEqual(SelectedOrdinals(FilesList)))errors.Add("详情视图订阅或选区迁移失败");
        DetailsMode.IsChecked=false;ToggleView(DetailsMode,new RoutedEventArgs());await Task.Delay(100);
        if(FilesList.ItemsSource is not null||FilesGrid.ItemsSource is null||!ranges.SequenceEqual(SelectedOrdinals(FilesGrid)))errors.Add("网格视图订阅或选区迁移失败");
        ClearResultSelection();
        var displayed=results!;var item=(await catalog!.ReadPage(resultHandle!.Id,0,1))[0];
        foreach(bool cancelRoot in new[]{false,true})
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var old=new VirtualResults(1,async(_,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return new[]{item};});
            int reads=0;using var next=new VirtualResults(1,(_,_)=>{reads++;return Task.FromResult<IReadOnlyList<SnapshotItem>>([item]);});
            var row=(FileRow)old[0]!;next.Retain(row,0,null);results=old;
            var loading=LoadRowProperties(row);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            results=next;await LoadRowProperties(row); // Existing request owns the row.
            if(cancelRoot)scanStop.Cancel();old.Dispose();
            await loading.WaitAsync(TimeSpan.FromSeconds(3));
            if(propertyRequests.Contains(row))errors.Add("属性请求未释放");
            if(!cancelRoot&&(row.Item is null||reads!=1))errors.Add("旧页取消后没有从新源接续");
            if(cancelRoot&&(row.Item is not null||reads!=0))errors.Add("切根取消后错误接续");
        }
        results=displayed;scanStop.Dispose();scanStop=new CancellationTokenSource();
        const string sentinel="新查询状态";
        verifyRetirementBarrier=()=>{queryRequest++;ResultSummary.Text=sentinel;DirectoryScopeLabel.Text=sentinel;ToolTipService.SetToolTip(DirectoryScopeLabel,sentinel);return Task.CompletedTask;};
        try{await RefreshQuery();}finally{verifyRetirementBarrier=null;}
        if(ResultSummary.Text!=sentinel||DirectoryScopeLabel.Text!=sentinel||!Equals(ToolTipService.GetToolTip(DirectoryScopeLabel),sentinel))errors.Add("退役等待后旧查询覆盖了新状态");
        report["errors"]=errors;
        if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));
        report["status"]="PASS";
    }
}
