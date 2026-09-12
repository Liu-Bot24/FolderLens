using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderLens.Core;
using FolderLens.Infrastructure;

namespace FolderLens.App;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class BrowserFileGroup:ObservableObject
{
    public SnapshotGroup Info {get;private set;}
    public string Title=>Info.RelativePath.Length==0?"本目录文件":Info.RelativePath;
    public string Summary=>$"{(Info.CapacityScope=="all"?"目录总容量":"匹配文件容量")} {FileRow.FormatBytes(Info.Bytes)} · 目录匹配 {Info.MatchCount:N0} 项 · 本组显示 {Info.Count:N0} 项{(Info.ScanState=="ready"?"":" · 部分统计")}";
    private readonly VirtualRangeCollection<FileRow> items;
    public IList Items=>items;
    public BrowserFileGroup(VirtualResults source,SnapshotGroup info)
    {
        Info=info;items=new(checked((int)info.Count),index=>(FileRow)source[checked((int)info.Start)+index]!,value=>source.IndexOf(value) is var position&&position>=0?checked(position-(int)info.Start):-1);
    }
    public void Update(VirtualResults source,SnapshotGroup info,IReadOnlyList<RangeEdit> changes)
    {
        var previous=Info;Info=info;
        items.UpdateRanges(changes,index=>(FileRow)source[checked((int)info.Start)+index]!,value=>source.IndexOf(value) is var position&&position>=0?checked(position-(int)info.Start):-1);
        if(previous.RelativePath!=info.RelativePath)OnPropertyChanged(nameof(Title));
        if(previous.Bytes!=info.Bytes||previous.MatchCount!=info.MatchCount||previous.Count!=info.Count||previous.ScanState!=info.ScanState)OnPropertyChanged(nameof(Summary));
    }
}
