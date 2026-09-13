using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool syncingBrowserSelection;
    private long browserSelectionRequest;
    private ListViewBase ActiveBrowser=>DetailsMode.IsChecked==true?FilesList:FilesGrid;
    private void ClipBrowserViewport(object sender,SizeChangedEventArgs args)
    {
        if(sender is FrameworkElement element)element.Clip=new RectangleGeometry{Rect=new(0,0,args.NewSize.Width,args.NewSize.Height)};
    }
    private async Task<bool> SelectBrowserOrdinal(VirtualResults source,int index,CancellationToken cancellation)
    {
        long request=++browserSelectionRequest;
        var row=(FileRow)source[index]!;await source.EnsureLoaded(row,cancellation);
        if(request!=browserSelectionRequest||closing||cancellation.IsCancellationRequested||!ReferenceEquals(results,source))return false;
        var view=ActiveBrowser;
        bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
        try{RevealBrowserRow(row);view.SelectedItem=row;view.ScrollIntoView(row);}finally{syncingBrowserSelection=prior;}
        var preview=SelectPreview(row);long adopted=browserSelectionRequest;await preview;
        return adopted==browserSelectionRequest&&!closing&&!cancellation.IsCancellationRequested&&ReferenceEquals(selected,row);
    }
    private HashSet<FileRow> firstPageRows=[];
    private void AttachBrowserView(object? source)
    {
        // A collapsed ListView still receives every notification and may build a
        // nonvirtual item cache. Only the displayed view owns a source subscription.
        bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
        if(!ReferenceEquals(source,flatBrowserItems))firstPageRows.Clear();
        try
        {
            if(DetailsMode.IsChecked==true){FilesList.ItemsSource=source;FilesGrid.ItemsSource=null;}
            else{FilesGrid.ItemsSource=source;FilesList.ItemsSource=null;}
        }
        finally{syncingBrowserSelection=prior;}
    }
    private IReadOnlyList<OrdinalRange> SelectedOrdinals(ListViewBase view)
    {
        var ranges=view.SelectedRanges.Select(range=>new OrdinalRange(range.FirstIndex,range.Length));
        if(browserGroups is null)return OrdinalSelection.Normalize(ranges,view.Items.Count);
        var mapped=new List<OrdinalRange>();long offset=0;
        foreach(var group in browserGroups)
        {
            if(group.IsCollapsed)continue;
            foreach(var range in ranges)
            {
                long start=Math.Max(offset,range.Start),end=Math.Min(offset+group.Info.Count,range.Start+range.Count);
                if(end>start)mapped.Add(new(group.Info.Start+start-offset,end-start));
            }
            offset+=group.Info.Count;
        }
        return OrdinalSelection.Normalize(mapped,results?.Count??view.Items.Count);
    }
    private FileRow? SelectionPreview(ListViewBase view)
    {
        if(syncingBrowserSelection||view.SelectedIndex<0)return null;
        int index=view.SelectedIndex;var ranges=view.SelectedRanges;
        for(var node=FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject;node is not null;node=VisualTreeHelper.GetParent(node))
        {
            if(node is FrameworkElement{DataContext:FileRow focused}&&view.Items.IndexOf(focused) is var focusedIndex&&focusedIndex>=0&&ranges.Any(range=>focusedIndex>=range.FirstIndex&&focusedIndex<(long)range.FirstIndex+range.Length))
            {index=focusedIndex;break;}
            if(ReferenceEquals(node,view))break;
        }
        return index<view.Items.Count?view.Items[index] as FileRow:null;
    }
    private void ToggleView(object sender,RoutedEventArgs args)
    {
        bool details=DetailsMode.IsChecked==true;
        UpdatePathPresentationControl();
        if(controlsReady&&!suppressFilters&&ReferenceEquals(sender,DetailsMode))categoryDetailViews[Tag(Category)]=details;
        var previous=details?(ListViewBase)FilesGrid:FilesList;
        var next=details?(ListViewBase)FilesList:FilesGrid;
        var ranges=previous.SelectedRanges.ToArray();
        object? source=previous.ItemsSource??next.ItemsSource;
        FilesGrid.Visibility=details?Visibility.Collapsed:Visibility.Visible;DetailsPane.Visibility=details?Visibility.Visible:Visibility.Collapsed;
        AttachBrowserView(source);
        if(results is null&&firstPageSequence.Length==0)return;
        syncingBrowserSelection=true;
        try
        {
            if(next.Items.Count>0)next.DeselectRange(new ItemIndexRange(0,(uint)next.Items.Count));
            foreach(var range in ranges)next.SelectRange(new ItemIndexRange(range.FirstIndex,range.Length));
            if(ranges.Length==0&&selected is not null)next.SelectedItem=selected;
        }
        finally{syncingBrowserSelection=false;}
        if(selected is not null)next.ScrollIntoView(selected);
    }
}
