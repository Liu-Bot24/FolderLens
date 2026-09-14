using System.Collections;
using FolderLens.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderLens.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed class FileRow : ObservableObject
{
    public long Ordinal {get;private set;}
    private string name="加载中…",path="",detail="";
    private ImageSource? thumbnail;
    private bool isCollected,quickCollectBusy;
    public bool IsCollected=>isCollected;
    public string CollectionGlyph=>isCollected?"\uE735":"\uE734";
    public string CollectionHint=>isCollected?"取消收藏（从所有收藏夹移除）":"快速收藏";
    public bool QuickCollectEnabled=>Item is not null&&!quickCollectBusy;
    public void SetCollected(bool value){if(SetProperty(ref isCollected,value)){OnPropertyChanged(nameof(IsCollected));OnPropertyChanged(nameof(CollectionGlyph));OnPropertyChanged(nameof(CollectionHint));}}
    public void SetQuickCollectBusy(bool value){quickCollectBusy=value;OnPropertyChanged(nameof(QuickCollectEnabled));}
    private string thumbnailError="";
    public string ThumbnailError {get=>thumbnailError;private set{if(SetProperty(ref thumbnailError,value)){OnPropertyChanged(nameof(ThumbnailErrorVisibility));OnPropertyChanged(nameof(FileIconVisibility));OnPropertyChanged(nameof(VideoBadgeVisibility));}}}
    public string ThumbnailErrorLabel=>Kind=="video"?"视频封面不可用":"缩略图不可用";
    public Visibility ThumbnailErrorVisibility=>ThumbnailError.Length==0?Visibility.Collapsed:Visibility.Visible;
    public void FailThumbnail(Exception error)
    {
        string reason=error switch
        {
            UnauthorizedAccessException=>"没有读取权限。",
            FileNotFoundException or DirectoryNotFoundException=>"原文件已不存在或目录暂不可用。",
            TimeoutException=>"读取超时。",
            InvalidDataException {Message:"UnsupportedCodec"}=>"暂不支持此编码。",
            InvalidDataException {Message:"DecodeFailed"}=>"无法解码；文件可能损坏或编码不受支持。",
            IOException {Message:"FileChanged"}=>"文件内容已变化。",
            _=>"读取失败："+error.Message
        };
        ThumbnailError=reason+" 按 F5 刷新后重试，或打开文件查看详情。";
    }
    private string resolution="未知",allocation="未知",modified="",duration="",format="";
    public string Resolution {get=>resolution;private set=>SetProperty(ref resolution,value);}
    public string AllocatedText {get=>allocation;private set=>SetProperty(ref allocation,value);}
    public string ModifiedText {get=>modified;private set=>SetProperty(ref modified,value);}
    public string DurationText {get=>duration;private set{if(SetProperty(ref duration,value)){OnPropertyChanged(nameof(VideoDurationText));OnPropertyChanged(nameof(VideoDescription));}}}
    public string VideoDurationText=>DurationText.Length==0?"时长未知":DurationText;
    public string VideoDescription=>"视频 · "+VideoDurationText;
    public Visibility VideoBadgeVisibility=>Kind=="video"&&ThumbnailError.Length==0?Visibility.Visible:Visibility.Collapsed;
    public string FormatText {get=>format;private set=>SetProperty(ref format,value);}
    public long? ModifiedUtcTicks {get;private set;}
    public string SourceSignature {get;private set;}="";
    public string? HydrationState {get;private set;}
    private string kind="other";
    public string Kind {get=>kind;private set{if(SetProperty(ref kind,value)){if(value=="other")Thumbnail=null;OnPropertyChanged(nameof(VideoBadgeVisibility));OnPropertyChanged(nameof(ThumbnailErrorLabel));OnPropertyChanged(nameof(FileIconVisibility));OnPropertyChanged(nameof(FileTypeLabel));OnPropertyChanged(nameof(FileTypeBadge));}}}
    public string SizeText=>FormatBytes(Item?.Bytes??0);
    private double cardWidth=144;
    private Visibility pathVisibility=Visibility.Collapsed;
    public double CardWidth {get=>cardWidth;private set{if(SetProperty(ref cardWidth,value))OnPropertyChanged(nameof(ThumbnailHeight));}}
    public double ThumbnailHeight=>Math.Round(CardWidth*0.68);
    public Visibility PathVisibility {get=>pathVisibility;private set=>SetProperty(ref pathVisibility,value);}
    public void SetPresentation(double width,bool showPath){CardWidth=width;PathVisibility=showPath?Visibility.Visible:Visibility.Collapsed;}
    public string Name {get=>name;private set=>SetProperty(ref name,value);}
    public string RelativePath {get=>path;private set=>SetProperty(ref path,value);}
    public string NavigationPath=>Item?.SourceRootPath is {} source?System.IO.Path.Combine(source,RelativePath):RelativePath;
    private bool collectionView;
    private string entryState="present";
    public string DisplayPath=>collectionView?NavigationPath:RelativePath;
    public string DisplayName=>Name+(entryState=="missing"?"（未找到）":entryState=="excluded"?"（未读取）":"");
    public void SetCollectionView(bool value){collectionView=value;OnPropertyChanged(nameof(DisplayPath));}
    public string Detail {get=>detail;private set=>SetProperty(ref detail,value);}
    public ImageSource? Thumbnail {get=>thumbnail;set{if(SetProperty(ref thumbnail,value))OnPropertyChanged(nameof(FileIconVisibility));}}
    public Visibility FileIconVisibility=>Item is not null&&Kind is not ("image" or "video")&&Thumbnail is null&&ThumbnailError.Length==0?Visibility.Visible:Visibility.Collapsed;
    public string FileTypeLabel=>FileTypeDisplay.Label(Name,Kind);
    public string FileTypeBadge=>FileTypeDisplay.Badge(Name,Kind);
    public SnapshotItem? Item {get;private set;}
    public FileRow(long ordinal)=>Ordinal=ordinal;
    public void Relocate(long ordinal,SnapshotGroup? group){Ordinal=ordinal;if(Item is not null)Item=Item with{Ordinal=ordinal,Group=group};}
    internal void AdoptObservation(SnapshotItem item)
    {
        if(Item is not {} old||old.EntryId!=item.EntryId||old.Version!=item.Version||old.RelativePath!=item.RelativePath||old.Bytes!=item.Bytes||old.Allocated!=item.Allocated||old.Kind!=item.Kind)
            throw new InvalidOperationException("只能为视觉等价的文件行更新快照观察值。");
        Item=item;Ordinal=item.Ordinal;OnPropertyChanged(nameof(QuickCollectEnabled));
    }
    public void Fill(SnapshotItem item){bool replaced=Item is null||Item.EntryId!=item.EntryId||Item.Version!=item.Version;Item=item;if(replaced){DurationText="";SetCollected(false);}OnPropertyChanged(nameof(QuickCollectEnabled));Name=System.IO.Path.GetFileName(item.RelativePath);RelativePath=item.RelativePath;Kind=item.Kind;OnPropertyChanged(nameof(DisplayName));OnPropertyChanged(nameof(DisplayPath));OnPropertyChanged(nameof(NavigationPath));Detail=$"{System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant()} · {FormatBytes(item.Bytes)}";FormatText=System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();OnPropertyChanged(nameof(FileIconVisibility));OnPropertyChanged(nameof(FileTypeLabel));OnPropertyChanged(nameof(FileTypeBadge));}
    public void Fail(Exception error){Name="加载失败";Detail=error.Message;}
    public void DescribeImage(int width,int height,string format)=>Detail=$"{width} × {height}  {format.ToUpperInvariant()}";
    public void UpdateProperties(FileProperties file,bool updateCollection=true)
    {
        if(Item is null||Item.EntryId!=file.EntryId||Item.Version!=file.Version)return;
        SourceSignature=file.SourceSignature;
        if(updateCollection)SetCollected(file.IsCollected);OnPropertyChanged(nameof(QuickCollectEnabled));
        Name=file.Name;RelativePath=file.RelativePath;Kind=file.Kind;ModifiedUtcTicks=file.ModifiedUtcTicks;HydrationState=file.HydrationState;
        entryState=file.EntryState;OnPropertyChanged(nameof(DisplayName));OnPropertyChanged(nameof(DisplayPath));OnPropertyChanged(nameof(NavigationPath));
        Resolution=file.Width is {} w&&file.Height is {} h?$"{w:N0} × {h:N0}":"未知";AllocatedText=file.AllocatedBytes is {} bytes?FormatBytes(bytes):"未知";
        ModifiedText=new DateTime(file.ModifiedUtcTicks,DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        DurationText=file.DurationMs is {} ms?$"{ms/3600000}:{ms/60000%60:D2}:{ms/1000%60:D2}":"";FormatText=(file.Format??System.IO.Path.GetExtension(file.Name).TrimStart('.')).ToUpperInvariant();
        Detail=$"{(file.Width is not null?Resolution:SizeText)}  {FormatText}{(file.Animated==true?" · 动图":"")}{(file.PageCount>1?$" · {file.PageCount} 页":"")}{(file.HydrationState=="placeholder"?" · 在线":"")}";OnPropertyChanged(nameof(SizeText));
    }
    public static string FormatBytes(long bytes)=>bytes>=1L<<30?$"{bytes/(double)(1L<<30):N2} GiB":bytes>=1<<20?$"{bytes/(double)(1<<20):N2} MiB":bytes>=1024?$"{bytes/1024.0:N1} KiB":$"{bytes} B";
}

public sealed partial class VirtualResults : IList,IDisposable
{
    private readonly DemandPageCache<SnapshotItem> data;
    private readonly CancellationTokenSource stop=new();
    private readonly Dictionary<int,FileRow[]> pages=[];
    private readonly LinkedList<int> recent=[];
    private sealed record SourcePosition(int Index);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FileRow,SourcePosition> positions=new();
    private readonly Dictionary<int,WeakReference<FileRow>> identities=[];
    private readonly Dictionary<int,FileRow> retained=[];
    private int pagesCreated;
    public IEnumerable<FileRow> CachedRows()=>identities.Values.Select(reference=>reference.TryGetTarget(out var row)?row:null).OfType<FileRow>();
    private void Register(FileRow row,int index){positions.Remove(row);positions.Add(row,new(index));identities[index]=new(row);shareCreated?.Invoke(row,index);}
    public void Retain(FileRow row,long ordinal,SnapshotGroup? group){int index=checked((int)ordinal);row.Relocate(ordinal,group);Register(row,index);retained[index]=row;}
    internal void Retain(FileRow row,SnapshotItem item){row.AdoptObservation(item);Retain(row,item.Ordinal,item.Group);}
    private bool disposed;
    public int Count {get;}
    private double cardWidth=144;
    private bool showPaths;
    private readonly bool collectionView;
    public void SetPresentation(double width,bool paths){cardWidth=width;showPaths=paths;foreach(var row in CachedRows())row.SetPresentation(width,paths);}
    public VirtualResults(CatalogStore catalog,ResultHandle handle,DispatcherQueue dispatcher)
        :this(checked((int)handle.Count),(page,token)=>catalog.ReadPage(handle.Id,page*256,256,token)){collectionView=handle.CollectionId is not null;}
    internal VirtualResults(int count,Func<int,CancellationToken,Task<IReadOnlyList<SnapshotItem>>> readPage)
    {
        Count=count;
        data=new(async(page,token)=>
        {
            var items=await readPage(page,token).ConfigureAwait(false);
            if(items.Count!=Math.Min(256,Count-page*256)||items.Where((item,index)=>item.Ordinal!=page*256L+index).Any())
                throw new InvalidDataException("结果页已不完整，请重新应用筛选。");
            return items;
        });
    }
    public object? this[int index]
    {
        get
        {
            if(index<0||index>=Count)throw new ArgumentOutOfRangeException(nameof(index));
            // A retiring WinUI adapter may finish reading its projection. These placeholders
            // cannot load: EnsureLoaded observes this source's canceled token before any I/O.
            if(disposed)return new FileRow(index);
            int page=index/256;
            if(!pages.TryGetValue(page,out var rows))
            {
                if(++pagesCreated%16==0)foreach(var key in identities.Where(pair=>!pair.Value.TryGetTarget(out _)).Select(pair=>pair.Key).ToArray())identities.Remove(key);
                rows=Enumerable.Range(page*256,Math.Min(256,Count-page*256)).Select(i=>
                {
                    retained.Remove(i,out var row);
                    if(row is null&&identities.TryGetValue(i,out var weak))weak.TryGetTarget(out row);
                    row??=resolveShared?.Invoke(i);
                    if(row is not null&&!positions.TryGetValue(row,out _))Register(row,i);
                    if(row is null){row=new FileRow(i);Register(row,i);}
                    row.SetCollectionView(collectionView);row.SetPresentation(cardWidth,showPaths);return row;
                }).ToArray();
                pages.Add(page,rows);recent.AddLast(page);
                // WinUI's grouped collection can retain a row after its cache page
                // is evicted. Row identity/lifetime must not depend on this LRU.
                while(pages.Count>16){int oldest=recent.First!.Value;recent.RemoveFirst();pages.Remove(oldest);}
            }
            else{recent.Remove(page);recent.AddLast(page);}
            // Enumeration is not a request to read/decode every file in a group.
            return rows[index%256];
        }
        set=>throw new NotSupportedException("Read-only results.");
    }
    public async Task EnsureLoaded(FileRow row,CancellationToken cancellation)
    {
        using var request=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,cancellation);
        request.Token.ThrowIfCancellationRequested();
        if(row.Item is not null)return;
        try
        {
            int position=IndexOf(row);if(position<0)throw new InvalidOperationException("文件行不属于此结果源。");
            var page=await data.Read(position/256,request.Token);
            request.Token.ThrowIfCancellationRequested();
            row.SetPresentation(cardWidth,showPaths);
            row.Fill(page[position%256]);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error){row.Fail(error);throw;}
    }
    public bool IsReadOnly=>true;public bool IsFixedSize=>true;public bool IsSynchronized=>false;public object SyncRoot=>this;
    public int IndexOf(object? value)=>value is FileRow row&&positions.TryGetValue(row,out var position)?position.Index:-1;
    public bool Contains(object? value)=>IndexOf(value)>=0;
    public IEnumerator GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    public void CopyTo(Array array,int index){for(int i=0;i<Count;i++)array.SetValue(this[i],index+i);}
    public int Add(object? value)=>throw new NotSupportedException();public void Clear()=>throw new NotSupportedException();public void Insert(int index,object? value)=>throw new NotSupportedException();public void Remove(object? value)=>throw new NotSupportedException();public void RemoveAt(int index)=>throw new NotSupportedException();
    public void Dispose(){if(disposed)return;disposed=true;stop.Cancel();data.Dispose();pages.Clear();recent.Clear();retained.Clear();identities.Clear();positions.Clear();}
}
