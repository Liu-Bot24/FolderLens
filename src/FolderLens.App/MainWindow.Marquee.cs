using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private ListViewBase? marqueeView;
    private object? marqueeSource;
    private Point marqueeStart;
    private uint marqueePointer;
    private bool marqueeDragging,marqueeAdditive;
    private OrdinalRange[] marqueeBaseline=[];
    private Border? marqueeBox;
    private Canvas? marqueeOverlay;

    private void InitializeMarquee()
    {
        marqueeBox=new Border{BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent)),Background=new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(45,0,120,215)),Visibility=Visibility.Collapsed};
        marqueeOverlay=new Canvas{IsHitTestVisible=false};marqueeOverlay.Children.Add(marqueeBox);Grid.SetRow(marqueeOverlay,2);BrowserPane.Children.Add(marqueeOverlay);
        foreach(var view in new ListViewBase[]{FilesList,FilesGrid})
        {
            view.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler(MarqueePressed),true);
            view.AddHandler(UIElement.PointerMovedEvent,new PointerEventHandler(MarqueeMoved),true);
            view.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler(MarqueeReleased),true);
            view.PointerCaptureLost+=(_,e)=>{if(ReferenceEquals(e.OriginalSource,view))CancelMarquee();};
            view.AddHandler(UIElement.PointerWheelChangedEvent,new PointerEventHandler((_,_)=>CancelMarquee()),true);
        }
    }
    private void MarqueePressed(object sender,PointerRoutedEventArgs e)
    {
        if(sender is not ListViewBase view||closing||immersive||!e.GetCurrentPoint(view).Properties.IsLeftButtonPressed||e.Pointer.PointerDeviceType!=Microsoft.UI.Input.PointerDeviceType.Mouse)return;
        if(e.OriginalSource is FrameworkElement {DataContext:BrowserFileGroup})return;
        // Leave buttons, scroll bars, text editors and group headers to their own commands.
        for(var node=e.OriginalSource as DependencyObject;node is not null&&!ReferenceEquals(node,view);node=VisualTreeHelper.GetParent(node))
        {
            if(node is ButtonBase or ScrollBar or TextBox)return;
            // Dragging a file belongs to Windows drag-and-drop. Blank space starts marquee selection.
            if(node is ListViewItem or GridViewItem)return;
        }
        CancelMarquee();
        marqueeView=view;marqueeSource=view.ItemsSource;marqueeStart=e.GetCurrentPoint(view).Position;marqueePointer=e.Pointer.PointerId;
        marqueeAdditive=(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)&CoreVirtualKeyStates.Down)!=0||
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)&CoreVirtualKeyStates.Down)!=0;
        marqueeBaseline=view.SelectedRanges.Select(r=>new OrdinalRange(r.FirstIndex,r.Length)).ToArray();
    }
    private void MarqueeMoved(object sender,PointerRoutedEventArgs e)
    {
        if(marqueeView is not {} view||!ReferenceEquals(sender,view)||e.Pointer.PointerId!=marqueePointer)return;
        var point=e.GetCurrentPoint(view);
        if(!point.Properties.IsLeftButtonPressed||!ReferenceEquals(view.ItemsSource,marqueeSource)){CancelMarquee();return;}
        if(!marqueeDragging)
        {
            if(Math.Abs(point.Position.X-marqueeStart.X)<5&&Math.Abs(point.Position.Y-marqueeStart.Y)<5)return;
            if(!view.CapturePointer(e.Pointer)){CancelMarquee();return;}
            marqueeDragging=true;
        }
        e.Handled=true;ApplyMarquee(view,point.Position);
    }
    private void ApplyMarquee(ListViewBase view,Point point)
    {
        // Work only with realized containers. No SQL, page loads, or enumeration
        // of all virtual rows is needed to select items in this viewport.
        double x=Math.Clamp(point.X,0,view.ActualWidth),y=Math.Clamp(point.Y,0,view.ActualHeight);
        var rect=new Rect(Math.Min(x,marqueeStart.X),Math.Min(y,marqueeStart.Y),Math.Abs(x-marqueeStart.X),Math.Abs(y-marqueeStart.Y));
        var hits=new List<OrdinalRange>();
        foreach(var pair in visibleContainers)
        {
            if(!ReferenceEquals(pair.Key.View,view)||pair.Key.Container is not FrameworkElement element)continue;
            var bounds=element.TransformToVisual(view).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight));
            if(bounds.Right<=rect.Left||bounds.Left>=rect.Right||bounds.Bottom<=rect.Top||bounds.Top>=rect.Bottom)continue;
            int index=view.IndexFromContainer(element);if(index>=0)hits.Add(new(index,1));
        }
        var ranges=OrdinalSelection.Normalize(marqueeAdditive?marqueeBaseline.Concat(hits):hits,view.Items.Count);
        bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
        try
        {
            foreach(var range in view.SelectedRanges.ToArray())view.DeselectRange(range);
            foreach(var range in ranges)view.SelectRange(new ItemIndexRange((int)range.Start,(uint)range.Count));
        }
        finally{syncingBrowserSelection=prior;}
        var origin=view.TransformToVisual(marqueeOverlay).TransformPoint(new Point(rect.X,rect.Y));
        Canvas.SetLeft(marqueeBox!,origin.X);Canvas.SetTop(marqueeBox!,origin.Y);marqueeBox!.Width=rect.Width;marqueeBox.Height=rect.Height;marqueeBox.Visibility=Visibility.Visible;
    }
    private async void MarqueeReleased(object sender,PointerRoutedEventArgs e)
    {
        if(marqueeView is not {} view||e.Pointer.PointerId!=marqueePointer)return;
        bool dragged=marqueeDragging;EndMarquee(false);
        if(!dragged)return;e.Handled=true;
        try{if(SelectionPreview(view) is {} row)await SelectPreview(row);else ClearResultSelection();}catch(Exception ex){ShowError(ex);}
    }
    private void EndMarquee(bool restore)
    {
        var view=marqueeView;bool dragged=marqueeDragging;bool canRestore=restore&&dragged&&view is not null&&ReferenceEquals(view.ItemsSource,marqueeSource);marqueeView=null;marqueeDragging=false;
        if(canRestore&&view is not null)
        {
            bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
            try{foreach(var range in view.SelectedRanges.ToArray())view.DeselectRange(range);foreach(var range in marqueeBaseline)view.SelectRange(new ItemIndexRange((int)range.Start,(uint)range.Count));}
            finally{syncingBrowserSelection=prior;}
        }
        marqueeSource=null;marqueeBaseline=[];if(marqueeBox is not null)marqueeBox.Visibility=Visibility.Collapsed;
        if(dragged)view?.ReleasePointerCaptures();
        if(canRestore)SyncMarqueeSelection(view!);
    }
    private void CancelMarquee()=>EndMarquee(true);
    private async void SyncMarqueeSelection(ListViewBase view)
    {
        if(closing||!ReferenceEquals(view,ActiveBrowser))return;
        try
        {
            if(SelectionPreview(view) is {} row){if(!ReferenceEquals(row,selected))await SelectPreview(row);}
            else ClearResultSelection();
        }
        catch(Exception error){if(!closing)ShowError(error);}
    }
}
