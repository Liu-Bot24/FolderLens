using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly ViewerGroupBoundary viewerGroupBoundary = new();
    private Border? viewerGroupNotice;
    private TextBlock? viewerGroupPath;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? viewerGroupTimer;
    private bool viewerMenuOpen;

    private void InitializeGroupNotice()
    {
        viewerGroupPath = new TextBlock
        {
            FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        viewerGroupNotice = new Border
        {
            Child = viewerGroupPath, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24, 0, 24, 80), Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(5), Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(210, 28, 28, 28))
        };
        PreviewSurface.Children.Add(viewerGroupNotice);
        viewerGroupTimer = DispatcherQueue.CreateTimer();
        viewerGroupTimer.Interval = TimeSpan.FromSeconds(2.5);
        viewerGroupTimer.IsRepeating = false;
        viewerGroupTimer.Tick += (_, _) => {if(!viewerMenuOpen)viewerGroupNotice.Visibility = Visibility.Collapsed;};
    }

    private void SetViewerMenuNotice(bool open)
    {
        viewerMenuOpen=open;viewerGroupTimer?.Stop();
        if(open)UpdateGroupNotice();else if(viewerGroupNotice is not null)viewerGroupNotice.Visibility=Visibility.Collapsed;
    }

    private void UpdateGroupNotice()
    {
        var group = selected?.Item?.Group;
        bool active = fullScreen && !closing && selected?.Kind is "image" or "video";
        // A paged row may not have its metadata yet. Hide the previous label
        // without inventing a group change; compare once the actual row arrives.
        if (fullScreen && !closing && selected is not null && selected.Item is null)
        {
            viewerGroupTimer?.Stop();
            if (viewerGroupNotice is not null) viewerGroupNotice.Visibility = Visibility.Collapsed;
            return;
        }
        bool changed = viewerGroupBoundary.Observe(active, resultHandle?.Id, group?.Id);
        if (viewerGroupNotice is null) return;
        if (!active || (!viewerMenuOpen&&(group is null || resultHandle is null)))
        {
            viewerGroupTimer?.Stop();
            viewerGroupNotice.Visibility = Visibility.Collapsed;
            return;
        }
        if (!changed&&!viewerMenuOpen) return;
        string directory=group?.RelativePath??Path.GetDirectoryName(selected!.RelativePath)??"";
        viewerGroupPath!.Text = "文件夹：" + (directory.Length == 0 ? "本目录文件" : directory);
        viewerGroupNotice.MaxWidth = Math.Max(120, Math.Min(720, PreviewSurface.ActualWidth - 48));
        viewerGroupNotice.Visibility = Visibility.Visible;
        viewerGroupTimer!.Stop();
        if(!viewerMenuOpen)viewerGroupTimer.Start();
    }
}
