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
    private VirtualRangeCollection<FileRow> items;
    private VirtualResults source;
    public bool IsCollapsed {get;private set;}
    public string ToggleGlyph=>IsCollapsed?"\uE76C":"\uE70D";
    public string ToggleLabel=>IsCollapsed?"展开此文件夹分组":"折叠此文件夹分组";
    public IList Items=>IsCollapsed?Array.Empty<FileRow>():items;
    public BrowserFileGroup(VirtualResults source,SnapshotGroup info)
    {
        this.source=source;Info=info;items=CreateItems();
    }
    private VirtualRangeCollection<FileRow> CreateItems()
    {
        var snapshot=source;var info=Info;
        return new(checked((int)info.Count),index=>(FileRow)snapshot[checked((int)info.Start)+index]!,value=>snapshot.IndexOf(value) is var position&&position>=0?checked(position-(int)info.Start):-1);
    }
    // Called while detached from the grouped view, so no per-file notifications
    // or materialization of hidden rows are needed.
    public void ToggleCollapsed()
    {
        IsCollapsed=!IsCollapsed;items=IsCollapsed?new(0,_=>throw new ArgumentOutOfRangeException()):CreateItems();
        OnPropertyChanged(nameof(ToggleGlyph));OnPropertyChanged(nameof(ToggleLabel));
    }
    public void Update(VirtualResults source,SnapshotGroup info,IReadOnlyList<RangeEdit> changes)
    {
        var previous=Info;this.source=source;Info=info;
        if(!IsCollapsed)
        {
            FileRow Read(int index)=>(FileRow)source[checked((int)info.Start)+index]!;
            int Locate(object? value)=>source.IndexOf(value) is var position&&position>=0?checked(position-(int)info.Start):-1;
            items.UpdateRanges(changes,Read,Locate);
        }
        if(previous.RelativePath!=info.RelativePath)OnPropertyChanged(nameof(Title));
        if(previous.Bytes!=info.Bytes||previous.MatchCount!=info.MatchCount||previous.Count!=info.Count||previous.ScanState!=info.ScanState)OnPropertyChanged(nameof(Summary));
    }
}
