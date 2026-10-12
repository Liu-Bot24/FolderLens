using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly HashSet<FrameworkElement> realizedFolderHeaders=[];
    private void FolderHeaderLoaded(object sender,RoutedEventArgs args)
    {
        if(sender is FrameworkElement header)realizedFolderHeaders.Add(header);
        QueueThumbnailViewport();
    }
    private void FolderHeaderUnloaded(object sender,RoutedEventArgs args)
    {
        if(sender is FrameworkElement header)realizedFolderHeaders.Remove(header);
        QueueThumbnailViewport();
    }
    private BrowserFileGroup? FolderGroupAt(long ordinal)
    {
        if(browserGroups is null)return null;
        int low=0,high=browserGroups.Count-1;
        while(low<=high)
        {
            int middle=low+(high-low)/2;var group=browserGroups[middle];
            if(ordinal<group.Info.Start)high=middle-1;
            else if(ordinal>=group.Info.Start+group.Info.Count)low=middle+1;
            else return group;
        }
        return null;
    }
    private void UpdateCurrentFolderHeader()
    {
        var view=ActiveBrowser;
        bool available=folderGrouping.Enabled&&activeCollectionId is null&&view.ItemsSource is not null
            &&(browserGroups is {Count:>0}||firstPageSequence.Length>0);
        CurrentFolderHeader.Visibility=available?Visibility.Visible:Visibility.Collapsed;
        if(!available){CurrentFolderHeader.DataContext=null;CurrentFolderTitle.Text="";CurrentFolderSummary.Text="";return;}

        BrowserFileGroup? current=null;FileRow? firstRow=null;double leading=double.MaxValue;
        double? VisibleTop(FrameworkElement element)
        {
            if(element.ActualHeight<=0)return null;
            var point=element.TransformToVisual(view).TransformPoint(new(0,0));
            return point.Y+element.ActualHeight>0&&point.Y<view.ActualHeight?Math.Max(0,point.Y):null;
        }
        foreach(var pair in visibleContainers)
        {
            if(!(firstPageRows.Contains(pair.Value)||results?.Contains(pair.Value)==true)||pair.Key.View!=view||pair.Key.Container is not FrameworkElement element||VisibleTop(element) is not {} top||top>=leading)continue;
            leading=top;firstRow=pair.Value;current=FolderGroupAt(firstRow.Ordinal);
        }
        // Headers themselves are the only visible content when groups are
        // collapsed, and can precede the first card at a folder boundary.
        foreach(var header in realizedFolderHeaders)
        {
            if(header.DataContext is not BrowserFileGroup group||!ReferenceEquals(FolderGroupAt(group.Info.Start),group))continue;
            DependencyObject? parent=header;
            while(parent is not null&&parent is not ListViewBase)parent=VisualTreeHelper.GetParent(parent);
            if(!ReferenceEquals(parent,view)||VisibleTop(header) is not {} top||top>leading)continue;
            leading=top;current=group;firstRow=null;
        }
        if(current is null&&firstRow is null)
        {
            int first=view.ItemsPanelRoot switch{ItemsWrapGrid panel=>panel.FirstVisibleIndex,ItemsStackPanel panel=>panel.FirstVisibleIndex,_=>-1};
            if(first>=0&&first<view.Items.Count&&view.Items[first] is FileRow row){firstRow=row;current=FolderGroupAt(row.Ordinal);}
        }
        CurrentFolderHeader.DataContext=current;
        CurrentFolderToggle.Visibility=current is null?Visibility.Collapsed:Visibility.Visible;
        if(current is not null)
        {
            CurrentFolderTitle.Text="当前目录："+current.Title;
            CurrentFolderSummary.Text=current.Summary;
            ToolTipService.SetToolTip(CurrentFolderTitle,current.Title);
            ToolTipService.SetToolTip(CurrentFolderSummary,current.Summary);
        }
        else
        {
            string relative=firstRow?.Item?.RelativePath??"";
            string directory=Path.GetDirectoryName(relative)??"";
            if(folderGrouping.Levels=="first")directory=directory.Split('\\','/')[0];
            string title=directory.Length==0?(relative.Length==0?"正在读取目录…":"本目录文件"):directory;
            CurrentFolderTitle.Text="当前目录："+title;
            CurrentFolderSummary.Text="正在整理目录容量和分组…";
            ToolTipService.SetToolTip(CurrentFolderTitle,title);
            ToolTipService.SetToolTip(CurrentFolderSummary,CurrentFolderSummary.Text);
        }
    }
}
