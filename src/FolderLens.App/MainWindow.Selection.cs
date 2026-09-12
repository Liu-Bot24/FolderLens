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
    private async Task<bool> SelectBrowserOrdinal(VirtualResults source,int index,CancellationToken cancellation)
    {
        long request=++browserSelectionRequest;
        var row=(FileRow)source[index]!;await source.EnsureLoaded(row,cancellation);
        if(request!=browserSelectionRequest||closing||cancellation.IsCancellationRequested||!ReferenceEquals(results,source))return false;
        var view=ActiveBrowser;
        bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
        try{view.SelectedIndex=index;view.ScrollIntoView(row);}finally{syncingBrowserSelection=prior;}
        var preview=SelectPreview(row);long adopted=browserSelectionRequest;await preview;
        return adopted==browserSelectionRequest&&!closing&&!cancellation.IsCancellationRequested&&ReferenceEquals(selected,row);
    }
    private void AttachBrowserView(object? source)
    {
        // A collapsed ListView still receives every notification and may build a
        // nonvirtual item cache. Only the displayed view owns a source subscription.
        bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
        try
        {
            if(DetailsMode.IsChecked==true){FilesList.ItemsSource=source;FilesGrid.ItemsSource=null;}
            else{FilesGrid.ItemsSource=source;FilesList.ItemsSource=null;}
        }
        finally{syncingBrowserSelection=prior;}
    }
    private static IReadOnlyList<OrdinalRange> SelectedOrdinals(ListViewBase view)=>OrdinalSelection.Normalize(view.SelectedRanges.Select(range=>new OrdinalRange(range.FirstIndex,range.Length)),view.Items.Count);
    private FileRow? SelectionPreview(ListViewBase view)
    {
        if(syncingBrowserSelection||view.SelectedIndex<0)return null;
        int index=view.SelectedIndex;var ranges=view.SelectedRanges;
        for(var node=FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject;node is not null;node=VisualTreeHelper.GetParent(node))
        {
            if(node is FrameworkElement{DataContext:FileRow focused}&&ranges.Any(range=>focused.Ordinal>=range.FirstIndex&&focused.Ordinal<(long)range.FirstIndex+range.Length))
            {index=checked((int)focused.Ordinal);break;}
            if(ReferenceEquals(node,view))break;
        }
        return index<view.Items.Count?view.Items[index] as FileRow:null;
    }
    private void ToggleView(object sender,RoutedEventArgs args)
    {
        bool details=DetailsMode.IsChecked==true;
        var previous=details?(ListViewBase)FilesGrid:FilesList;
        var next=details?(ListViewBase)FilesList:FilesGrid;
        var ranges=SelectedOrdinals(previous);
        object? source=previous.ItemsSource??next.ItemsSource;
        FilesGrid.Visibility=details?Visibility.Collapsed:Visibility.Visible;DetailsPane.Visibility=details?Visibility.Visible:Visibility.Collapsed;
        AttachBrowserView(source);
        if(results is null)return;
        syncingBrowserSelection=true;
        try
        {
            if(next.Items.Count>0)next.DeselectRange(new ItemIndexRange(0,(uint)next.Items.Count));
            foreach(var range in ranges)next.SelectRange(new ItemIndexRange((int)range.Start,(uint)range.Count));
            if(ranges.Count==0&&selected is not null)next.SelectedIndex=(int)selected.Ordinal;
        }
        finally{syncingBrowserSelection=false;}
        if(selected is not null)next.ScrollIntoView(selected);
    }
}
