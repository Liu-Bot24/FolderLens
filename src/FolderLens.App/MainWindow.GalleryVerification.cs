using System.Diagnostics;
using System.Text.Json;
using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using FolderLens.Infrastructure;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Action<string,double>? verifyThumbnailStage;
    private async Task VerifyGallery(string source,Dictionary<string,object> report)
    {
        source=PathRules.ValidateSource(source);
        string output=Path.GetFullPath(dataDirectory);
        if(output.Equals(source,StringComparison.OrdinalIgnoreCase)||output.StartsWith(source.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("验证数据必须放在源目录以外。");
        folderGrouping=new(Environment.GetCommandLineArgs().Contains("--verify-gallery-grouped"));UpdateGroupingButton();
        var watch=Stopwatch.StartNew();double firstRows=-1,firstImage=-1;int frames=0,cleared=0,replaced=0;string phase="scanning";
        var stages=new Dictionary<string,(int Count,double Total,double Max)>();
        verifyThumbnailStage=(stage,ms)=>{string key=phase+"/"+stage;var prior=stages.GetValueOrDefault(key);stages[key]=(prior.Count+1,prior.Total+ms,Math.Max(prior.Max,ms));};
        object? priorSource=null;var faults=new List<object>();var releases=new Queue<object>();int rebindings=0;
        var publicationFailures=new List<object>();
        var publicationDurations=new List<double>();verifyPublicationDuration=ms=>publicationDurations.Add(ms);
        var publicationStages=new List<IReadOnlyDictionary<string,double>>();verifyPublicationStages=sample=>publicationStages.Add(sample);
        verifyPublicationFailure=error=>publicationFailures.Add(new{ms=watch.Elapsed.TotalMilliseconds,phase,queryRequest,notification=error.Data["FolderLens.RangeNotification"],error=error.ToString()});
        verifyRowRecycling=(row,reason)=>{releases.Enqueue(new{ms=watch.Elapsed.TotalMilliseconds,phase,ordinal=row.Ordinal,queryRequest,queryBusy,updatingBrowser,reason});while(releases.Count>24)releases.Dequeue();};
        var observed=new Dictionary<FileRow,(object? Image,DependencyObject Container)>();
        (FileRow Row,double Top)? stationaryAnchor=null;int stationaryMissing=0,stationaryMoved=0;
        int reservedSlots=Environment.GetCommandLineArgs().Contains("--verify-gallery-two-slots")?Math.Max(0,WorkerResources.Shared.ThumbnailConcurrency-2):0;
        for(int i=0;i<reservedSlots;i++)await thumbnailSlots.WaitAsync();
        long peakProcessTree=0;
        List<(FileRow Row,DependencyObject Container)> Viewport()
        {
            var rows=new List<(FileRow,DependencyObject)>();
            foreach(var item in visibleContainers)
            {
                if(item.Key.View!=FilesGrid||item.Key.Container is not FrameworkElement element||element.ActualHeight<=0||results?.IndexOf(item.Value)<0)continue;
                var point=element.TransformToVisual(FilesGrid).TransformPoint(new(0,0));
                if(point.Y+element.ActualHeight>0&&point.Y<FilesGrid.ActualHeight&&point.X+element.ActualWidth>0&&point.X<FilesGrid.ActualWidth)rows.Add((item.Value,item.Key.Container));
            }
            return rows;
        }
        void Frame(object? sender,object args)
        {
            frames++;var viewport=Viewport();peakProcessTree=Math.Max(peakProcessTree,WorkerResources.Shared.Snapshot.ProcessTreeBytes);
            if(!ReferenceEquals(priorSource,FilesGrid.ItemsSource)){if(priorSource is not null)rebindings++;priorSource=FilesGrid.ItemsSource;}
            if(firstRows<0&&viewport.Any(item=>item.Row.Item is not null))firstRows=watch.Elapsed.TotalMilliseconds;
            if(firstImage<0&&viewport.Any(item=>item.Row.Thumbnail is not null))firstImage=watch.Elapsed.TotalMilliseconds;
            if(phase=="scanning")
            {
                if(stationaryAnchor is {} anchor&&results?.Contains(anchor.Row)==true)
                {
                    var entry=viewport.FirstOrDefault(item=>ReferenceEquals(item.Row,anchor.Row));
                    if(entry.Container is not FrameworkElement element)stationaryMissing++;
                    else if(Math.Abs(element.TransformToVisual(FilesGrid).TransformPoint(new(0,0)).Y-anchor.Top)>2)stationaryMoved++;
                }
                else stationaryAnchor=CapturePublicationViewport(FilesGrid);
            }
            var current=viewport.Select(item=>item.Row).ToHashSet();
            foreach(var old in observed.Keys.Where(row=>!current.Contains(row)).ToArray())observed.Remove(old);
            foreach(var item in viewport)
            {
                if(observed.TryGetValue(item.Row,out var prior)&&prior.Image is not null)
                {
                    if(item.Row.Thumbnail is null)cleared++;
                    if(!ReferenceEquals(prior.Container,item.Container))replaced++;
                    if((item.Row.Thumbnail is null||!ReferenceEquals(prior.Container,item.Container))&&faults.Count<40)
                        faults.Add(new{ms=watch.Elapsed.TotalMilliseconds,phase,ordinal=item.Row.Ordinal,queryRequest,queryBusy,updatingBrowser,rebindings,thumbnailNull=item.Row.Thumbnail is null,oldContainer=prior.Container.GetHashCode(),newContainer=item.Container.GetHashCode(),viewportDuplicates=viewport.Count(entry=>ReferenceEquals(entry.Row,item.Row)),releases=releases.ToArray()});
                }
                observed[item.Row]=(item.Row.Thumbnail,item.Container);
            }
        }
        CompositionTarget.Rendering+=Frame;
        var screens=new List<object>();
        try
        {
            await OpenRoot(source).WaitAsync(TimeSpan.FromMinutes(2));
            report["scanAndInitialQueryMs"]=watch.Elapsed.TotalMilliseconds;
            report["indexedImageCount"]=resultHandle?.Count??0;
            if(results is null||results.Count==0)throw new InvalidOperationException("真实图片目录没有产生浏览结果。");
            foreach(double fraction in new[]{0d,.25,.5,.9,0})
            {
                phase="scroll-"+fraction;
                int index=(int)((results.Count-1)*fraction);var target=(FileRow)results[index]!;
                FilesGrid.ScrollIntoView(target,ScrollIntoViewAlignment.Leading);var timer=Stopwatch.StartNew();
                int items=0,ready=0,errors=0,loaded=0;bool arrived=false;
                while(timer.Elapsed<TimeSpan.FromSeconds(8))
                {
                    var viewport=Viewport();items=viewport.Count;ready=viewport.Count(item=>item.Row.Thumbnail is not null);errors=viewport.Count(item=>item.Row.ThumbnailError.Length>0);loaded=viewport.Count(item=>item.Row.Item is not null);
                    arrived=viewport.Any(item=>ReferenceEquals(item.Row,target));
                    if(arrived&&items>0&&ready+errors==items)break;
                    await Task.Delay(25);
                }
                screens.Add(new{fraction,index,elapsedMs=timer.Elapsed.TotalMilliseconds,items,loaded,ready,errors,arrived});
                Console.WriteLine($"Gallery viewport {fraction:P0}: {ready}/{items} thumbnails, {loaded} rows, {errors} errors, {timer.ElapsedMilliseconds}ms");
                if(!arrived||items==0||loaded<items||ready+errors<items)throw new InvalidOperationException("实际可见区域在8秒内仍有未完成的文件行或缩略图。");
                await Task.Delay(250);
            }
            if(Environment.GetCommandLineArgs().Contains("--verify-gallery-capture"))
            {
                var capture=new RenderTargetBitmap();await capture.RenderAsync(Shell);
                var pixels=(await capture.GetPixelsAsync()).ToArray();
                using var file=File.Create(Path.Combine(dataDirectory,"gallery.png"));using var stream=file.AsRandomAccessStream();
                var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)capture.PixelWidth,(uint)capture.PixelHeight,96,96,pixels);
                await encoder.FlushAsync();
                report["captureScope"]="Actual XAML RenderTargetBitmap; does not capture the Win2D swap chain or physical cursor.";
            }
            // Recycled containers are safe only with the same image and position
            // and no theme animation. Identity alone is not a visible-frame change.
            bool animatedRecycling=FilesGrid.ItemContainerTransitions is {Count:>0}||FilesList.ItemContainerTransitions is {Count:>0};
            report["fileListThemeAnimationsDisabled"]=!animatedRecycling;
            report["status"]=cleared==0&&!animatedRecycling&&stationaryMissing==0&&stationaryMoved==0?"PASS":"FAIL";
            if(cleared>0||animatedRecycling||stationaryMissing>0||stationaryMoved>0)throw new InvalidOperationException("后台更新导致视口跳位、消失、缩略图被清空或重复触发主题动画。");
        }
        finally
        {
            CompositionTarget.Rendering-=Frame;
            if(reservedSlots>0)thumbnailSlots.Release(reservedSlots);
            report["thumbnailConcurrency"]=WorkerResources.Shared.ThumbnailConcurrency-reservedSlots;
            report["peakProcessTreeBytes"]=peakProcessTree;
            report["publicationStages"]=publicationStages;
            verifyRowRecycling=null;verifyPublicationFailure=null;verifyPublicationDuration=null;verifyPublicationStages=null;
            verifyThumbnailStage=null;report["thumbnailStages"]=stages.ToDictionary(pair=>pair.Key,pair=>new{count=pair.Value.Count,totalMs=pair.Value.Total,meanMs=pair.Value.Total/pair.Value.Count,maxMs=pair.Value.Max});
            report["publicationDurationsMs"]=publicationDurations;report["grouped"]=folderGrouping.Enabled;
            report["publicationFailures"]=publicationFailures;report["faults"]=faults;report["itemsSourceRebindings"]=rebindings;
            report["firstRowsMs"]=firstRows;report["firstThumbnailOnRenderingMs"]=firstImage;report["screens"]=screens;
            report["frames"]=frames;report["continuousVisibleThumbnailClears"]=cleared;report["continuousVisibleContainerChanges"]=replaced;
            report["stationaryAnchorMissingFrames"]=stationaryMissing;report["stationaryAnchorMovedFrames"]=stationaryMoved;
            report["hostPeakWorkingSetBytes"]=Process.GetCurrentProcess().PeakWorkingSet64;
            report["measurementScope"]="Read-only real source; fresh app catalog/cache; OS cache not cleared; offscreen WinUI Rendering observations, not physical display Present timing. No source paths or image content in this report.";
        }
    }
    private async Task VerifyGroupPublication(byte[] png,Dictionary<string,object> report)
    {
        var source=new VirtualResults(catalog!,new("group-publication","root",1,1,900,0,0),DispatcherQueue);results=source;
        using var stream=new MemoryStream(png);var bitmap=new BitmapImage();await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        for(int i=0;i<256;i++)((FileRow)source[i]!).Thumbnail=bitmap;
        SnapshotGroup[] groups=[new("a","A",0,300,0,300,"ready","all"),new("b","B",0,300,300,300,"ready","all"),new("c","C",0,300,600,300,"ready","all")];
        BindBrowserResults(source,groups);await Task.Delay(100);
        var row=(FileRow)source[0]!;var container=FilesGrid.ContainerFromItem(row);
        if(container is null)throw new InvalidOperationException("分组首行尚未实现。");
        SnapshotGroup[] reordered=[groups[0],groups[2] with{Start=300},groups[1] with{Start=600}];
        UpdateBrowserResults(source,reordered,groups.ToDictionary(g=>g.Id,_=>(IReadOnlyList<RangeEdit>)Array.Empty<RangeEdit>()));
        await Task.Delay(100);
        report["unchangedFirstGroupRetainsThumbnail"]=ReferenceEquals(row.Thumbnail,bitmap);
        report["unchangedFirstGroupRetainsContainer"]=ReferenceEquals(container,FilesGrid.ContainerFromItem(row));
        if(!ReferenceEquals(row.Thumbnail,bitmap)||!ReferenceEquals(container,FilesGrid.ContainerFromItem(row)))throw new InvalidOperationException("只移动屏幕外的组，却重建了首组可见图片。");
        ToggleFolderGroup(browserGroups![1]);Shell.UpdateLayout();
        if(!ReferenceEquals(row.Thumbnail,bitmap)||FilesGrid.ContainerFromItem(row) is null)throw new InvalidOperationException("折叠其他组清除了当前可见图片。");
        var stableView=FilesGrid.ItemsSource;var stableGroup=browserGroups[0];
        var next=new VirtualResults(40900,(_,_)=>throw new InvalidOperationException("屏幕外文件不应读取。"));
        foreach(var retained in source.CachedRows().Where(item=>item.Ordinal<256).ToArray())next.Retain(retained,checked((int)retained.Ordinal),null);
        var enlarged=reordered.ToArray();enlarged[2]=enlarged[2] with{Count=40300,MatchCount=40300};
        var edits=enlarged.ToDictionary(group=>group.Id,group=>(IReadOnlyList<RangeEdit>)(group.Id==enlarged[2].Id?[new RangeEdit(300,0,40000)]:Array.Empty<RangeEdit>()));
        int frames=0,missing=0;
        void ObserveBulk(object? sender,object args){frames++;if(!ReferenceEquals(row.Thumbnail,bitmap)||FilesGrid.ContainerFromItem(row) is null)missing++;}
        CompositionTarget.Rendering+=ObserveBulk;
        try
        {
            var anchor=CapturePublicationViewport(FilesGrid);publicationRows=visible.ToHashSet();updatingBrowser=true;results=next;
            try{UpdateBrowserResults(next,enlarged,edits);RestorePublicationViewport(FilesGrid,anchor);}
            finally{updatingBrowser=false;ReleasePublicationRows();}
            await Task.Delay(150);
            if(!ReferenceEquals(stableView,FilesGrid.ItemsSource)||!ReferenceEquals(stableGroup,browserGroups[0])||!browserGroups[1].IsCollapsed||FilesGrid.Items.Count!=40600)
                throw new InvalidOperationException("大批量更新未保留视图、组对象、折叠状态或正确数量。");
            if(frames==0||missing!=0)throw new InvalidOperationException($"大批量更新空白帧：{missing}/{frames}。");
            if(next.CachedRows().Count()>1024)throw new InvalidOperationException("大批量更新提前创建屏幕外文件行。");
            report["bulkFrames"]=frames;report["bulkMissingFrames"]=missing;report["bulkRetainsGroupsAndCollapse"]=true;report["bulkRowsCreated"]=next.CachedRows().Count();
        }
        finally{CompositionTarget.Rendering-=ObserveBulk;source.Dispose();}
        report["status"]="PASS";
    }
    private async Task VerifyNativeRanges(Dictionary<string,object> report)
    {
        var checks=new List<object>();bool failed=false;
        foreach(string view in new[]{"grid","list","both"})foreach(bool single in new[]{false,true})
        {
            var probe=new NotificationHistoryFixture();
            FilesGrid.ItemsSource=view=="list"?null:probe;FilesList.ItemsSource=view=="grid"?null:probe;
            await Task.Delay(30);
            try{probe.AppendFour(single);probe.RemoveAt(4);checks.Add(new{history=view,single,status="PASS"});}
            catch(ArgumentException error){if(single)failed=true;checks.Add(new{history=view,single,status=single?"FAIL":"RANGE_REJECTED",error=error.Message});}
            FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;
        }
        foreach(string mode in new[]{"standard","no-gc","keep-sources","pin-current"})
        {
        var retired=new List<VirtualResults>();object[]? pinned=null;int step=0;
        VirtualResults sequenceSource=new(catalog!,new("sequence0","root",1,1,10000,0,0),DispatcherQueue);
        var sequenceInitial=sequenceSource;
        var sequence=new VirtualRangeCollection<FileRow>(sequenceSource.Count,i=>(FileRow)sequenceInitial[i]!,sequenceInitial.IndexOf);
        FilesGrid.ItemsSource=sequence;FilesList.ItemsSource=sequence;await Task.Delay(50);
        try
        {
            for(step=1;step<=6;step++)
            {
                var previous=sequenceSource;int prefix=previous.Count/3,removed=previous.Count/4,added=removed+8192;
                if(mode=="pin-current")pinned=Enumerable.Range(0,previous.Count).Select(i=>previous[i]!).ToArray();
                var next=new VirtualResults(catalog!,new($"sequence{step}","root",1,step+1,previous.Count+8192,0,0),DispatcherQueue);
                var changes=new Dictionary<string,IReadOnlyList<RangeEdit>>{{"",[new(prefix,removed,added)]}};
                using(previous.ShareUnchangedRowsWith(next,VirtualResults.SharedRanges(previous.Count,next.Count,[],[],changes)))
                    sequence.UpdateRanges(changes[""],i=>(FileRow)next[i]!,next.IndexOf);
                sequenceSource=next;if(mode=="keep-sources")retired.Add(previous);else previous.Dispose();
                if(mode!="no-gc"){GC.Collect();GC.WaitForPendingFinalizers();}await Task.Delay(50);GC.KeepAlive(pinned);
            }
            checks.Add(new{mode,sequence=6,status="PASS"});
        }
        catch(Exception error){failed=true;checks.Add(new{mode,step,status="FAIL",error=error.ToString(),notification=error.Data["FolderLens.RangeNotification"]});}
        finally{FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;sequenceSource.Dispose();foreach(var item in retired)item.Dispose();}
        }
        foreach(bool virtualSource in new[]{false,true})
        {
            using var oldSource=new VirtualResults(catalog!,new("native-old","root",1,1,42186,0,0),DispatcherQueue);
            using var nextSource=new VirtualResults(catalog!,new("native-next","root",1,2,50250,0,0),DispatcherQueue);
            var change=new RangeEdit(14275,6008,14072);
            var old=virtualSource?null:Enumerable.Range(0,oldSource.Count).Select(i=>new FileRow(i)).ToArray();
            var next=old?.Take(change.Prefix).Concat(Enumerable.Range(0,change.Added).Select(i=>new FileRow(i+change.Prefix))).Concat(old.Skip(change.Prefix+change.Removed)).ToArray();
            FileRow ReadOld(int i)=>old is null?(FileRow)oldSource[i]!:old[i];
            FileRow ReadNext(int i)=>next is null?(FileRow)nextSource[i]!:next[i];
            int OldIndex(object? v)=>old is null?oldSource.IndexOf(v):Array.IndexOf(old,v);
            int NextIndex(object? v)=>next is null?nextSource.IndexOf(v):Array.IndexOf(next,v);
            var list=new VirtualRangeCollection<FileRow>(oldSource.Count,ReadOld,OldIndex);
            FilesGrid.ItemsSource=list;FilesList.ItemsSource=list;await Task.Delay(100);
            var changes=new Dictionary<string,IReadOnlyList<RangeEdit>>{{"",[change]}};
            try
            {
                using var bridge=oldSource.ShareUnchangedRowsWith(nextSource,VirtualResults.SharedRanges(oldSource.Count,nextSource.Count,[],[],changes));
                list.UpdateRanges([change],ReadNext,NextIndex);
                checks.Add(new{virtualSource,status="PASS"});
            }
            catch(Exception error){failed=true;checks.Add(new{virtualSource,status="FAIL",error=error.ToString(),notification=error.Data["FolderLens.RangeNotification"]});}
            FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;
        }
        foreach(int size in new[]{100,10000,36000})
        {
            var old=Enumerable.Range(0,size).Select(index=>new FileRow(index)).ToArray();
            foreach(var change in new[]{new RangeEdit(0,0,10),new RangeEdit(size/2,0,10),new RangeEdit(size,0,10),new RangeEdit(10,size-20,20)})
            {
                var next=old.Take(change.Prefix).Concat(Enumerable.Range(0,change.Added).Select(index=>new FileRow(change.Prefix+index))).Concat(old.Skip(change.Prefix+change.Removed)).ToArray();
                var oldPositions=old.Select((row,index)=>(row,index)).ToDictionary(item=>item.row,item=>item.index);
                var nextPositions=next.Select((row,index)=>(row,index)).ToDictionary(item=>item.row,item=>item.index);
                var list=new VirtualRangeCollection<FileRow>(size,index=>old[index],value=>value is FileRow row?oldPositions.GetValueOrDefault(row,-1):-1);
                FilesGrid.ItemsSource=list;FilesList.ItemsSource=list;await Task.Delay(50);
                try
                {
                    list.UpdateRanges([change],index=>next[index],value=>value is FileRow row?nextPositions.GetValueOrDefault(row,-1):-1);
                    if(FilesGrid.Items.Count!=next.Length)throw new InvalidOperationException("原生视图计数不一致。");
                    checks.Add(new{size,change,status="PASS"});
                }
                catch(Exception error){failed=true;checks.Add(new{size,change,status="FAIL",error=error.ToString()});}
            }
        }
        foreach(var spec in new[]{(45770,new RangeEdit(21184,8221,15773)),(84074,new RangeEdit(57386,26495,32639))})
        {
            var old=Enumerable.Range(0,spec.Item1).Select(i=>new FileRow(i)).ToArray();var change=spec.Item2;
            var next=old.Take(change.Prefix).Concat(Enumerable.Range(0,change.Added).Select(i=>new FileRow(change.Prefix+i))).Concat(old.Skip(change.Prefix+change.Removed)).ToArray();
            var oldMap=old.Select((row,i)=>(row,i)).ToDictionary(p=>p.row,p=>p.i);var newMap=next.Select((row,i)=>(row,i)).ToDictionary(p=>p.row,p=>p.i);
            var list=new VirtualRangeCollection<FileRow>(old.Length,i=>old[i],v=>v is FileRow r?oldMap.GetValueOrDefault(r,-1):-1);
            FilesGrid.ItemsSource=list;FilesList.ItemsSource=list;await Task.Delay(50);
            try{list.UpdateRanges([change],i=>next[i],v=>v is FileRow r?newMap.GetValueOrDefault(r,-1):-1);checks.Add(new{size=old.Length,change,status="PASS"});}
            catch(Exception error){failed=true;checks.Add(new{size=old.Length,change,status="FAIL",error=error.ToString()});}
        }
        foreach(bool bothVisible in new[]{false,true})foreach(bool duplicate in new[]{false,true})
        {
            DetailsPane.Visibility=bothVisible?Visibility.Visible:Visibility.Collapsed;
            var old=Enumerable.Range(0,3).Select(i=>new FileRow(i)).ToArray();
            FileRow[] next=duplicate?[old[2],old[1]]:[old[0],new(1),old[1],old[2],new(4)];
            RangeEdit[] edits=duplicate?[new(0,1,1),new(2,1,0)]:[new(1,0,1),new(3,0,1)];
            var list=new VirtualRangeCollection<FileRow>(3,i=>old[i],v=>Array.IndexOf(old,v));
            FilesGrid.ItemsSource=list;FilesList.ItemsSource=list;await Task.Delay(50);
            try{list.UpdateRanges(edits,i=>next[i],v=>Array.IndexOf(next,v));checks.Add(new{bothVisible,duplicate,status="PASS"});}
            catch(Exception error){failed=true;checks.Add(new{bothVisible,duplicate,status="FAIL",error=error.ToString()});}
        }
        DetailsPane.Visibility=Visibility.Collapsed;
        report["checks"]=checks;
        report["status"]=failed?"FAIL":"PASS";
        if((string)report["status"]=="FAIL")throw new InvalidOperationException("原生范围通知反例失败。");
    }
    private sealed class NotificationHistoryFixture:System.Collections.ObjectModel.ObservableCollection<FileRow>
    {
        public NotificationHistoryFixture()=>Add(new(0));
        public void AppendFour(bool single)
        {
            var items=Enumerable.Range(1,4).Select(i=>new FileRow(i)).ToArray();
            if(single){foreach(var item in items)Add(item);return;}
            foreach(var item in items)Items.Add(item);
            OnPropertyChanged(new(nameof(Count)));OnPropertyChanged(new("Item[]"));
            OnCollectionChanged(new(System.Collections.Specialized.NotifyCollectionChangedAction.Add,items,1));
        }
    }
}
