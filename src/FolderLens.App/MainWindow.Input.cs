using System.Numerics;
using System.Runtime.InteropServices;
using FolderLens.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private enum ViewerAction { Previous,Next,First,Last,Left,Right,Up,Down,Fit,Actual,ZoomIn,ZoomOut,FitWidth,FitHeight,AutomaticSizing,LockSizing,Rotate,RotateCounterclockwise,CopyFilePath,Help,ToggleFullScreen,ToggleBrowser,ToggleWindowViewer,ReturnBrowser,ContextMenu,Find,OpenFolder,FocusPath,Refresh,Slideshow,BackFolder,ForwardFolder,ParentFolder }
    private enum ViewerSizing { Automatic,Locked }
    private enum ViewerScaleIntent { Fit,Width,Height,Custom }
    private enum ViewerGesture { None,Pressed,Dragging,Magnifier }
    private sealed record ViewerBinding(VirtualKey Key,VirtualKeyModifiers Modifiers,ViewerAction Action);
    private static readonly ViewerBinding[] ViewerBindings=
    [
        new(VirtualKey.Left,VirtualKeyModifiers.Menu,ViewerAction.BackFolder),new(VirtualKey.Right,VirtualKeyModifiers.Menu,ViewerAction.ForwardFolder),new(VirtualKey.Up,VirtualKeyModifiers.Menu,ViewerAction.ParentFolder),
        new(VirtualKey.F,VirtualKeyModifiers.Control,ViewerAction.Find),new(VirtualKey.O,VirtualKeyModifiers.Control,ViewerAction.OpenFolder),new(VirtualKey.L,VirtualKeyModifiers.Control,ViewerAction.FocusPath),new(VirtualKey.F5,0,ViewerAction.Refresh),new(VirtualKey.Space,VirtualKeyModifiers.Control,ViewerAction.Slideshow),
        new(VirtualKey.F11,0,ViewerAction.ToggleFullScreen),new(VirtualKey.Escape,0,ViewerAction.ReturnBrowser),new(VirtualKey.Enter,0,ViewerAction.ToggleBrowser),
        new(VirtualKey.Left,0,ViewerAction.Left),new(VirtualKey.Right,0,ViewerAction.Right),new(VirtualKey.Up,0,ViewerAction.Up),new(VirtualKey.Down,0,ViewerAction.Down),
        new(VirtualKey.PageDown,0,ViewerAction.Next),new(VirtualKey.Space,0,ViewerAction.Next),new(VirtualKey.PageUp,0,ViewerAction.Previous),new(VirtualKey.Back,0,ViewerAction.Previous),
        new(VirtualKey.Home,0,ViewerAction.First),new(VirtualKey.End,0,ViewerAction.Last),new(VirtualKey.B,0,ViewerAction.Fit),new(VirtualKey.Multiply,0,ViewerAction.Fit),
        new(VirtualKey.Add,0,ViewerAction.ZoomIn),new((VirtualKey)187,0,ViewerAction.ZoomIn),new((VirtualKey)187,VirtualKeyModifiers.Shift,ViewerAction.ZoomIn),
        new(VirtualKey.Subtract,0,ViewerAction.ZoomOut),new((VirtualKey)189,0,ViewerAction.ZoomOut),new(VirtualKey.Number8,VirtualKeyModifiers.Shift,ViewerAction.Fit),
        new(VirtualKey.Number0,VirtualKeyModifiers.Control,ViewerAction.Actual),new(VirtualKey.NumberPad0,VirtualKeyModifiers.Control,ViewerAction.Actual),
        new(VirtualKey.W,VirtualKeyModifiers.Shift,ViewerAction.FitWidth),new(VirtualKey.H,VirtualKeyModifiers.Shift,ViewerAction.FitHeight),
        new(VirtualKey.K,VirtualKeyModifiers.Control|VirtualKeyModifiers.Shift,ViewerAction.AutomaticSizing),new(VirtualKey.L,VirtualKeyModifiers.Control|VirtualKeyModifiers.Shift,ViewerAction.LockSizing),
        new(VirtualKey.F1,0,ViewerAction.Help),new(VirtualKey.C,VirtualKeyModifiers.Control|VirtualKeyModifiers.Shift,ViewerAction.CopyFilePath),new(VirtualKey.R,VirtualKeyModifiers.Shift,ViewerAction.RotateCounterclockwise),
        new(VirtualKey.R,0,ViewerAction.Rotate),new(VirtualKey.F10,VirtualKeyModifiers.Shift,ViewerAction.ContextMenu),new(VirtualKey.Application,0,ViewerAction.ContextMenu)
    ];
    private ViewerSizing viewerSizing=ViewerSizing.Automatic;
    private ViewerScaleIntent viewerScaleIntent=ViewerScaleIntent.Fit;
    private double viewerLockedPhysicalScale=1,viewerCustomPhysicalScale=1;
    private long viewerSizedSelection=-1,viewerGestureSelection,viewerGestureRevision;
    private ViewerGesture viewerGesture;
    private uint? viewerPointerId;
    private Point viewerDownPoint,viewerLastPoint,viewerTapPoint;
    private bool viewerDownOverflow,viewerPreferencesLoading,lensLoading,lensPending,viewerFindPending;
    private static readonly TimeSpan ViewerHoldDelay=TimeSpan.FromMilliseconds(200);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? viewerTapTimer,viewerHoldTimer;
    private long viewerPressedAt;
    private bool viewerPointerMoved;
    private CancellationTokenSource? lensStop;
    private readonly Dictionary<(int X,int Y),CanvasBitmap> lensTiles=[];
    private Vector2 lensNativeCenter;
    private readonly SemaphoreSlim viewerPreferencesGate=new(1,1);
    private MenuFlyout? viewerContextMenu;
    private sealed record ViewerPreferences(string Wheel="next",string Sizing="auto",double LockedPhysicalScale=1,PressZoomOptions? PressZoom=null,int PressZoomVersion=0);

    private void InitializeViewerInput()
    {
        // Accelerators and routed keys share the same action table and guard, including focus in a Win2D surface.
        foreach(var binding in ViewerBindings)
        {
            var accelerator=new KeyboardAccelerator{Key=binding.Key,Modifiers=binding.Modifiers};
            accelerator.Invoked+=async(_,args)=>
            {
                if(!CanRunViewerBinding(binding,FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject))return;
                args.Handled=true;await RunViewerAction(binding.Action);
            };
            Shell.KeyboardAccelerators.Add(accelerator);
        }
        viewerHoldTimer=DispatcherQueue.CreateTimer();viewerHoldTimer.IsRepeating=false;viewerHoldTimer.Interval=ViewerHoldDelay;
        viewerHoldTimer.Tick+=(_,_)=>
        {
            if(!closing&&viewerPointerId is not null&&viewerGesture==ViewerGesture.Pressed&&viewerGestureSelection==selection)
                StartViewerMagnifier();
        };
        // A released short click also waits for double-click disambiguation.
        viewerTapTimer=DispatcherQueue.CreateTimer();viewerTapTimer.IsRepeating=false;viewerTapTimer.Interval=TimeSpan.FromMilliseconds(Math.Max(200,GetDoubleClickTime()));
        viewerTapTimer.Tick+=async(_,_)=>
        {
            if(viewerGestureSelection!=selection||closing)return;
            if(zoom>0)await RunViewerAction(ViewerAction.Fit);else await ZoomViewerAt(1/Shell.XamlRoot.RasterizationScale,viewerTapPoint);
        };
        ImageInput.DoubleTapped+=ViewerDoubleTapped;
        ImageInput.RightTapped+=ViewerRightTapped;
        ImageInput.PointerCanceled+=(_,_)=>ResetViewerGesture();
        ImageInput.PointerCaptureLost+=(_,_)=>{if(viewerPointerId is not null)ResetViewerGesture();};
        ImageInput.PointerExited+=(_,_)=>{if(viewerPointerId is null)PreviewSurface.SetViewerCursor(ViewerCursorFeedback.Arrow);};
        PreviewSurface.MagnifierDraw+=DrawViewerMagnifier;
        TextToolsFlyout.Opened+=(_,_)=>{if(viewerFindPending){viewerFindPending=false;TextQuery.Focus(FocusState.Keyboard);TextQuery.SelectAll();}};
        PreviewSurface.InputSurface=ImageInput;ImageInput.IsTabStop=true;UpdateViewerCursor();
        BuildViewerContextMenu();
    }
    private async void ViewerKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Handled)return;var modifiers=ViewerModifiers();var binding=ViewerBindings.FirstOrDefault(b=>b.Key==e.Key&&b.Modifiers==modifiers);
        if(binding is null||!CanRunViewerBinding(binding,e.OriginalSource as DependencyObject))return;
        e.Handled=true;await RunViewerAction(binding.Action);
    }
    private bool CanRunViewerBinding(ViewerBinding binding,DependencyObject? source)
    {
        if(closing||ViewerPopupOwnsInput(source))return false;
        if(binding.Action is ViewerAction.Help or ViewerAction.Find or ViewerAction.OpenFolder or ViewerAction.FocusPath or ViewerAction.Refresh or ViewerAction.BackFolder or ViewerAction.ForwardFolder or ViewerAction.ParentFolder)return true;
        if(binding.Action==ViewerAction.ToggleFullScreen)return selected is not null||results?.Count>0;
        if(binding.Action is ViewerAction.ReturnBrowser)return immersive||fullScreen;
        if(ViewerEditorOwnsInput(source))return false;
        // Native browser controls own navigation, paging and activation keys.
        // Viewer shortcuts must not turn a page-down in a large list into one file.
        if(!immersive&&!fullScreen&&!IsInViewerSurface(source))
        {
            if(binding.Action is ViewerAction.Previous or ViewerAction.Next or ViewerAction.First or ViewerAction.Last or ViewerAction.Left or ViewerAction.Right or ViewerAction.Up or ViewerAction.Down)return false;
            if(binding.Action==ViewerAction.ToggleBrowser&&!IsInFileList(source))return false;
        }
        if(binding.Action is ViewerAction.ToggleBrowser)return selected is not null||results?.Count>0;
        if(selected is null)return false;
        if(binding.Action==ViewerAction.CopyFilePath)return selected.Item is not null;
        if(selected.Kind=="video")return binding.Action is ViewerAction.Previous or ViewerAction.Next or ViewerAction.First or ViewerAction.Last or ViewerAction.Left or ViewerAction.Right or ViewerAction.ContextMenu;
        if(selected.Kind!="image")return false;
        if(binding.Action is ViewerAction.Left or ViewerAction.Right or ViewerAction.Up or ViewerAction.Down)
            return immersive||IsInViewerSurface(source);
        return true;
    }
    private bool ViewerPopupOwnsInput(DependencyObject? source)
    {
        source??=FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject;
        for(var node=source;node is not null;node=VisualTreeHelper.GetParent(node))
            if(node is ContentDialog or MenuFlyoutPresenter or MenuFlyoutItemBase||node is ComboBox{IsDropDownOpen:true})return true;
        return false;
    }
    private bool ViewerEditorOwnsInput(DependencyObject? source)
    {
        source??=FocusManager.GetFocusedElement(Shell.XamlRoot) as DependencyObject;bool editor=false;
        for(var node=source;node is not null;node=VisualTreeHelper.GetParent(node))
        {
            if(node is FrameworkElement{Visibility:Visibility.Collapsed})return false; // Stale focus in hidden browser chrome must not trap fullscreen keys.
            if(node is TextBox or RichEditBox or PasswordBox or NumberBox or ComboBox or Slider or AutoSuggestBox)editor=true;
        }
        return editor;
    }
    private bool IsInViewerSurface(DependencyObject? source)
    {for(var node=source;node is not null;node=VisualTreeHelper.GetParent(node))if(ReferenceEquals(node,PreviewSurface))return true;return false;}
    private bool IsInFileList(DependencyObject? source)
    {for(var node=source;node is not null;node=VisualTreeHelper.GetParent(node))if(ReferenceEquals(node,FilesGrid)||ReferenceEquals(node,FilesList))return true;return false;}
    private static VirtualKeyModifiers ViewerModifiers()
    {
        VirtualKeyModifiers result=0;
        foreach(var key in new[]{(VirtualKey.Control,VirtualKeyModifiers.Control),(VirtualKey.Shift,VirtualKeyModifiers.Shift),(VirtualKey.Menu,VirtualKeyModifiers.Menu),(VirtualKey.LeftWindows,VirtualKeyModifiers.Windows),(VirtualKey.RightWindows,VirtualKeyModifiers.Windows)})
            if(InputKeyboardSource.GetKeyStateForCurrentThread(key.Item1).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))result|=key.Item2;
        return result;
    }
    private async Task RunViewerAction(ViewerAction action)
    {
        try
        {
            ResetViewerGesture();
            switch(action)
            {
                case ViewerAction.Help: await ShowLocalHelp();return;
                case ViewerAction.CopyFilePath: CopyPath(this,new());return;
                case ViewerAction.Find:
                    if(immersive&&selected is not null&&selected.Kind is "text" or "markdown")
                    {viewerFindPending=true;TextToolsFlyout.ShowAt(PreviewSurface);}
                    else{await ReturnToBrowser();Search.Focus(FocusState.Keyboard);Search.SelectAll();}return;
                case ViewerAction.OpenFolder: await ReturnToBrowser();PickRoot(this,new());return;
                case ViewerAction.FocusPath: await ReturnToBrowser();RootPath.Focus(FocusState.Keyboard);RootPath.SelectAll();return;
                case ViewerAction.BackFolder: await NavigateHistory(false);return;
                case ViewerAction.ForwardFolder: await NavigateHistory(true);return;
                case ViewerAction.ParentFolder: await ReturnToBrowser();ParentRoot(this,new());return;
                case ViewerAction.Refresh: RefreshRoot(this,new());return;
                case ViewerAction.Slideshow: ToggleSlideshow(this,new());return;
                case ViewerAction.ToggleFullScreen: if(fullScreen)await LeaveFullScreen(toBrowser:false);else await EnterFullScreen();return;
                case ViewerAction.ToggleBrowser: if(immersive||fullScreen)await ReturnToBrowser();else await EnterFullScreen();return;
                case ViewerAction.ToggleWindowViewer: if(immersive||fullScreen)await ReturnToBrowser();else{await SetImmersive(true);ImageInput.Focus(FocusState.Programmatic);}return;
                case ViewerAction.ReturnBrowser: await ReturnToBrowser();return;
                case ViewerAction.Previous: Navigate(-1);return;
                case ViewerAction.Next: Navigate(1);return;
                case ViewerAction.First: if(selected is not null)Navigate(-(int)selected.Ordinal);return;
                case ViewerAction.Last: if(selected is not null&&results is not null)Navigate(results.Count-1-(int)selected.Ordinal);return;
                case ViewerAction.ContextMenu: ShowViewerContextMenu(new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));return;
            }
            if(selected?.Kind=="video")
            {
                if(action==ViewerAction.Left)Navigate(-1);else if(action==ViewerAction.Right)Navigate(1);
                return;
            }
            if(fitBitmap is null||sourceWidth<=0||sourceHeight<=0||previewLoading)return;
            switch(action)
            {
                case ViewerAction.Left: case ViewerAction.Right: case ViewerAction.Up: case ViewerAction.Down:
                    if(ViewerHasOverflow())
                    {float step=(float)Math.Max(32,Math.Min(ImageCanvas.ActualWidth,ImageCanvas.ActualHeight)*.08);PanViewerScreen(action==ViewerAction.Left?new(step,0):action==ViewerAction.Right?new(-step,0):action==ViewerAction.Up?new(0,step):new(0,-step));await RefreshViewerPixels();}
                    else if(action==ViewerAction.Left)Navigate(-1);else if(action==ViewerAction.Right)Navigate(1);return;
                case ViewerAction.Fit: viewerScaleIntent=ViewerScaleIntent.Fit;zoom=0;pan=Vector2.Zero;break;
                case ViewerAction.Actual: if(offlinePreview){QualityLabel.Text="原文件离线，无法读取真正的100%。";return;}await ZoomViewerAt(1/Shell.XamlRoot.RasterizationScale,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));return;
                case ViewerAction.ZoomIn: await ZoomViewerAt(EffectiveScale()*1.2,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));return;
                case ViewerAction.ZoomOut: await ZoomViewerAt(EffectiveScale()/1.2,new(ImageCanvas.ActualWidth/2,ImageCanvas.ActualHeight/2));return;
                case ViewerAction.FitWidth: viewerScaleIntent=ViewerScaleIntent.Width;ApplyViewerScaleIntent();PanViewerToStart(vertical:true);break;
                case ViewerAction.FitHeight: viewerScaleIntent=ViewerScaleIntent.Height;ApplyViewerScaleIntent();PanViewerToStart(vertical:false);break;
                case ViewerAction.AutomaticSizing: viewerSizing=ViewerSizing.Automatic;viewerScaleIntent=ViewerScaleIntent.Fit;zoom=0;pan=Vector2.Zero;await SaveViewerPreferences();break;
                case ViewerAction.LockSizing: viewerSizing=ViewerSizing.Locked;viewerLockedPhysicalScale=EffectiveScale()*Shell.XamlRoot.RasterizationScale;viewerScaleIntent=ViewerScaleIntent.Custom;viewerCustomPhysicalScale=viewerLockedPhysicalScale;await SaveViewerPreferences();break;
                case ViewerAction.Rotate: case ViewerAction.RotateCounterclockwise: if(selected?.Kind!="image")return;rotation=(rotation+(action==ViewerAction.Rotate?1:3))%4;if(viewerScaleIntent is ViewerScaleIntent.Width or ViewerScaleIntent.Height)ApplyViewerScaleIntent();break;
            }
            RememberViewerScale();ClampPan();await RefreshViewerPixels();
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}
    }
    private async Task ReturnToBrowser()
    {slideShow=false;slideTimer?.Stop();if(fullScreen)await LeaveFullScreen(toBrowser:true);else if(immersive)await SetImmersive(false);}
    private bool ViewerHasOverflow()
    {if(sourceWidth<=0||sourceHeight<=0||Shell.XamlRoot is null)return false;double scale=EffectiveScale();return (rotation%2==0?sourceWidth:sourceHeight)*scale>ImageCanvas.ActualWidth+.5||(rotation%2==0?sourceHeight:sourceWidth)*scale>ImageCanvas.ActualHeight+.5;}
    private void PanViewerScreen(Vector2 change){pan+=Vector2.TransformNormal(change,Matrix3x2.CreateRotation(-rotation*(float)Math.PI/2));ClampPan();}
    private string EffectiveViewerWheelBehavior()=>wheelBehavior=="next"&&viewerScaleIntent is (ViewerScaleIntent.Width or ViewerScaleIntent.Height)&&ViewerHasOverflow()?"pan":wheelBehavior;
    private void PanViewerToStart(bool vertical)
    {double scale=EffectiveScale(),extent=vertical?(rotation%2==0?sourceHeight:sourceWidth)*scale-ImageCanvas.ActualHeight:(rotation%2==0?sourceWidth:sourceHeight)*scale-ImageCanvas.ActualWidth;pan=Vector2.Zero;PanViewerScreen(vertical?new(0,(float)Math.Max(0,extent/2)):new((float)Math.Max(0,extent/2),0));}
    private async Task ZoomViewerAt(double scale,Point at)
    {
        if(fitBitmap is null||previewLoading)return;double old=EffectiveScale();if(!double.IsFinite(old)||old<=0)return;
        zoom=Math.Clamp(scale,.01/Shell.XamlRoot.RasterizationScale,16/Shell.XamlRoot.RasterizationScale);
        Vector2 offset=Vector2.TransformNormal(new((float)(at.X-ImageCanvas.ActualWidth/2),(float)(at.Y-ImageCanvas.ActualHeight/2)),Matrix3x2.CreateRotation(-rotation*(float)Math.PI/2));
        pan=offset-(offset-pan)*(float)(zoom/old);viewerScaleIntent=ViewerScaleIntent.Custom;RememberViewerScale();ClampPan();await RefreshViewerPixels();
    }
    private void RememberViewerScale()
    {viewerCustomPhysicalScale=EffectiveScale()*Shell.XamlRoot.RasterizationScale;if(viewerSizing==ViewerSizing.Locked){viewerLockedPhysicalScale=viewerCustomPhysicalScale;_=SaveViewerPreferences();}}
    private async Task RefreshViewerPixels()
    {ImageCanvas.Invalidate();UpdateViewerCursor();UpdateViewerInformation();if(offlinePreview){QualityLabel.Text="离线缓存缩略图 · 原文件暂不可用";return;}if(zoom>0)await LoadVisibleTiles();else QualityLabel.Text=rawPreviewOnly?"相机内嵌预览 · 原始开发未完成":"清晰适屏";}
    private void ApplyViewerScaleIntent()
    {
        zoom=viewerScaleIntent switch{ViewerScaleIntent.Fit=>0,ViewerScaleIntent.Width=>ImageCanvas.ActualWidth/(rotation%2==0?sourceWidth:sourceHeight),ViewerScaleIntent.Height=>ImageCanvas.ActualHeight/(rotation%2==0?sourceHeight:sourceWidth),_=>viewerCustomPhysicalScale/Shell.XamlRoot.RasterizationScale};
    }
    private void ApplyViewerSizing()
    {
        if(sourceWidth<=0||sourceHeight<=0)return;
        if(selected?.Kind=="video"){zoom=0;pan=Vector2.Zero;rotation=0;ImageCanvas.Invalidate();UpdateViewerCursor();UpdateViewerInformation();return;}
        if(viewerSizedSelection!=selection)
        {viewerSizedSelection=selection;ResetViewerGesture();rotation=0;pan=Vector2.Zero;viewerScaleIntent=viewerSizing==ViewerSizing.Locked?ViewerScaleIntent.Custom:ViewerScaleIntent.Fit;viewerCustomPhysicalScale=viewerLockedPhysicalScale;}
        ApplyViewerScaleIntent();ClampPan();ImageCanvas.Invalidate();UpdateViewerCursor();UpdateViewerInformation();if(zoom>0&&!previewLoading)_=LoadVisibleTiles();
    }
    private void UpdateViewerCursor()=>PreviewSurface.SetViewerCursor(selected?.Kind=="image"&&(viewerGesture==ViewerGesture.Dragging||ViewerHasOverflow())?ViewerCursorFeedback.Pan:ViewerCursorFeedback.Arrow);
    private void WakeViewerCursor(Point point)
    {PreviewSurface.SetCursorHidden(false);viewerIdleTimer?.Stop();UpdateViewerCursor();if(fullScreen&&!viewerPanelsVisible&&viewerPointerId is null)viewerIdleTimer?.Start();}
    private async void ViewerWheel(object sender,PointerRoutedEventArgs e)
    {
        if(e.Handled||selected is null||selected.Kind is not ("image" or "video"))return;
        var point=e.GetCurrentPoint(ImageCanvas);int delta=point.Properties.MouseWheelDelta;if(delta==0)return;e.Handled=true;ResetViewerGesture();WakeViewerCursor(point.Position);
        try
        {
            bool control=(ViewerModifiers()&VirtualKeyModifiers.Control)!=0;
            if(selected.Kind=="video")
            {
                if(control)return;
                wheelRemainder+=delta;if(Math.Abs(wheelRemainder)>=120){wheelRemainder=0;await RunViewerAction(delta>0?ViewerAction.Previous:ViewerAction.Next);}return;
            }
            string effectiveWheel=EffectiveViewerWheelBehavior();
            if(!control&&effectiveWheel=="next")
            {wheelRemainder+=delta;if(Math.Abs(wheelRemainder)>=120){int steps=Math.Clamp(wheelRemainder/120,-8,8);wheelRemainder-=steps*120;await RunViewerAction(steps>0?ViewerAction.Previous:ViewerAction.Next);}return;}
            if(!control&&effectiveWheel=="pan")
            {bool horizontal=point.Properties.IsHorizontalMouseWheel||wheelBehavior=="next"&&viewerScaleIntent==ViewerScaleIntent.Height;PanViewerScreen(horizontal?new(delta*.75f,0):new(0,delta*.75f));await RefreshViewerPixels();return;}
            await ZoomViewerAt(EffectiveScale()*Math.Pow(1.2,delta/120.0),point.Position);
        }
        catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}
    }
    private async void ViewerPointerDown(object sender,PointerRoutedEventArgs e)
    {
        var point=e.GetCurrentPoint(ImageCanvas);
        if(point.Properties.IsMiddleButtonPressed){e.Handled=true;await RunViewerAction(ViewerAction.ToggleBrowser);return;}
        if(!IsInViewerSurface(e.OriginalSource as DependencyObject))return;
        if(!point.Properties.IsLeftButtonPressed||selected?.Kind!="image"||fitBitmap is null||previewLoading)return;
        e.Handled=true;viewerTapTimer?.Stop();ResetViewerGesture(cancelTap:false,keepDetails:true);ImageInput.Focus(FocusState.Pointer);
        ImageInput.CapturePointer(e.Pointer);WakeViewerCursor(point.Position);BeginViewerPress(e.Pointer.PointerId,point.Position);
    }
    private void BeginViewerPress(uint pointerId,Point position)
    {
        viewerPointerId=pointerId;viewerGesture=ViewerGesture.Pressed;viewerGestureSelection=selection;viewerDownPoint=viewerLastPoint=position;viewerDownOverflow=ViewerHasOverflow();
        viewerPressedAt=System.Diagnostics.Stopwatch.GetTimestamp();viewerPointerMoved=false;
        viewerHoldTimer?.Start();
    }
    private void ViewerPointerMove(object sender,PointerRoutedEventArgs e)
    {
        var point=e.GetCurrentPoint(ImageCanvas);WakeViewerCursor(point.Position);
        if(viewerPointerId!=e.Pointer.PointerId)return;e.Handled=true;
        if(viewerGesture is ViewerGesture.Pressed or ViewerGesture.Magnifier)
        {
            double threshold=Math.Max(3,GetSystemMetrics(68)/Shell.XamlRoot.RasterizationScale);
            if(Math.Abs(point.Position.X-viewerDownPoint.X)>threshold||Math.Abs(point.Position.Y-viewerDownPoint.Y)>threshold)
            {
                viewerPointerMoved=true;
                if(viewerDownOverflow)
                {viewerHoldTimer?.Stop();lensStop?.Cancel();lensPending=false;PreviewSurface.HideMagnifier();viewerGesture=ViewerGesture.Dragging;}
            }
        }
        if(viewerGesture==ViewerGesture.Dragging){PanViewerScreen(new((float)(point.Position.X-viewerLastPoint.X),(float)(point.Position.Y-viewerLastPoint.Y)));ImageCanvas.Invalidate();UpdateViewerCursor();}
        viewerLastPoint=point.Position;if(viewerGesture==ViewerGesture.Magnifier)UpdateViewerMagnifier();
    }
    private async void ViewerPointerUp(object sender,PointerRoutedEventArgs e)
    {
        if(viewerPointerId!=e.Pointer.PointerId)return;e.Handled=true;await CompleteViewerPress(e.GetCurrentPoint(ImageCanvas).Position);
    }
    private async Task CompleteViewerPress(Point position)
    {
        var completed=viewerGesture;viewerTapPoint=position;
        bool tap=completed==ViewerGesture.Pressed&&!viewerPointerMoved&&System.Diagnostics.Stopwatch.GetElapsedTime(viewerPressedAt)<ViewerHoldDelay;
        ResetViewerGesture(cancelTap:false,keepDetails:true);
        if(tap){viewerGestureSelection=selection;viewerTapTimer?.Start();}
        else if(completed==ViewerGesture.Dragging)await RefreshViewerPixels();
    }
    private async void ViewerDoubleTapped(object sender,DoubleTappedRoutedEventArgs e)
    {if(selected?.Kind=="video"){e.Handled=true;ExternalOpen(sender,new());return;}if(!immersive&&!fullScreen)return;e.Handled=true;ResetViewerGesture();await RunViewerAction(ViewerAction.ReturnBrowser);}
    private void ViewerRightTapped(object sender,RightTappedRoutedEventArgs e)
    {if(selected is null)return;e.Handled=true;ResetViewerGesture();ShowViewerContextMenu(e.GetPosition(ImageCanvas));}
    private void ResetViewerGesture(bool cancelTap=true,bool keepDetails=false)
    {
        viewerHoldTimer?.Stop();if(cancelTap)viewerTapTimer?.Stop();viewerGestureRevision++;
        viewerGesture=ViewerGesture.None;viewerPointerId=null;ImageInput.ReleasePointerCaptures();
        lensStop?.Cancel();lensStop?.Dispose();lensStop=null;lensPending=false;PreviewSurface.HideMagnifier();if(!keepDetails){foreach(var tile in lensTiles.Values)tile.Dispose();lensTiles.Clear();}UpdateViewerCursor();
    }
    private void StartViewerMagnifier()
    {
        if(viewerGestureSelection!=selection||fitBitmap is null||previewLoading||offlinePreview)return;
        PauseAnimationForDetail();viewerGesture=ViewerGesture.Magnifier;wholeImageHold=viewerPressZoom.UsesWholeImage(immersive||fullScreen);lensStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token);UpdateViewerMagnifier();
    }
    private void UpdateViewerMagnifier()
    {
        double scale=EffectiveScale();var offset=Vector2.TransformNormal(new((float)(viewerLastPoint.X-ImageCanvas.ActualWidth/2),(float)(viewerLastPoint.Y-ImageCanvas.ActualHeight/2)),Matrix3x2.CreateRotation(-rotation*(float)Math.PI/2));
        lensNativeCenter=(offset-pan)/(float)scale+new Vector2((float)sourceWidth/2,(float)sourceHeight/2);
        double side=Math.Min(280,1024/Shell.XamlRoot.RasterizationScale);
        if(wholeImageHold)
        {
            // Keep the pointed source pixel under the pointer, then clamp at image edges.
            lensNativeCenter-=offset/(float)PressZoomScale;
            var center=PressZoomOptions.ClampCenter(lensNativeCenter.X,lensNativeCenter.Y,sourceWidth,sourceHeight,ImageCanvas.ActualWidth,ImageCanvas.ActualHeight,PressZoomScale,rotation);
            lensNativeCenter=new((float)center.X,(float)center.Y);
        }
        PreviewSurface.ShowMagnifier(ImageCanvas.TransformToVisual(PreviewSurface).TransformPoint(viewerLastPoint),wholeImageHold?ImageCanvas.ActualWidth:side,wholeImageHold?ImageCanvas.ActualHeight:side,wholeImageHold);
        PreviewSurface.InvalidateMagnifier();
        lensPending=true;_=LoadMagnifierPixels();
    }
    private HashSet<(int X,int Y)> MagnifierTileKeys()
    {
        var range=ImageViewport.VisibleTiles(sourceWidth,sourceHeight,PreviewSurface.MagnifierWidth,PreviewSurface.MagnifierHeight,PressZoomScale,(sourceWidth/2-lensNativeCenter.X)*PressZoomScale,(sourceHeight/2-lensNativeCenter.Y)*PressZoomScale,rotation,padding:0);
        if(range.Count>128)throw new InvalidDataException("当前放大区域超过原图读取预算，请提高放大比例。");
        var keys=new HashSet<(int,int)>();for(int y=range.FirstY;y<=range.LastY;y++)for(int x=range.FirstX;x<=range.LastX;x++)keys.Add((x,y));return keys;
    }
    private async Task LoadMagnifierPixels()
    {
        if(lensLoading||lensStop is null||selected?.Item is null)return;lensLoading=true;long revision=viewerGestureRevision,current=selection;var token=lensStop.Token;
        try
        {
            if(rawPreviewOnly)
            {
                if(!await EnsureDevelopedRaw(selected,current,token)){if(revision==viewerGestureRevision&&current==selection){lensPending=false;PreviewSurface.SetMagnifierQuality("相机预览 · 原图不可用");}return;}
                if(revision!=viewerGestureRevision||current!=selection)return;UpdateViewerMagnifier();
            }
            while(lensPending&&viewerGesture==ViewerGesture.Magnifier&&revision==viewerGestureRevision)
            {
                lensPending=false;var keys=MagnifierTileKeys();foreach(var key in lensTiles.Keys.Where(k=>!keys.Contains(k)).ToArray()){lensTiles[key].Dispose();lensTiles.Remove(key);}
                bool animated=AnimationButton.Visibility==Visibility.Visible;
                PreviewSurface.SetMagnifierQuality($"正在读取原图 · {viewerPressZoom.Percent:0.##}%");
                foreach(var key in keys)
                {
                    if(lensTiles.ContainsKey(key))continue;token.ThrowIfCancellationRequested();
                    var bitmap=!animated&&imagePage==0?await ReadPrefetchedDetail(selected,key,token):null;
                    if(bitmap is null)
                    {
                        var reply=await previewWorker!.Request(Path.Combine(root,selected.RelativePath),"fullTile",Context(selected,current),new(1024,1024,FrameIndex:animated?animationFrameIndex:0,TileX:key.X,TileY:key.Y,PageIndex:imagePage),token,Stamp(selected));
                        bitmap=await LoadRenderedBitmap(reply);
                    }
                    if(token.IsCancellationRequested||revision!=viewerGestureRevision||current!=selection){bitmap.Dispose();return;}
                    if(MagnifierTileKeys().Contains(key))lensTiles[key]=bitmap;else bitmap.Dispose();PreviewSurface.InvalidateMagnifier();if(lensPending)break;
                }
                if(!lensPending)PreviewSurface.SetMagnifierQuality($"{viewerPressZoom.Percent:0.##}% · 原图细节");
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){if(revision==viewerGestureRevision){lensPending=false;PreviewSurface.SetMagnifierQuality($"无法读取原像素：{ex.Message}");}}
        finally{lensLoading=false;if(lensPending&&viewerGesture==ViewerGesture.Magnifier&&lensStop is not null)_=LoadMagnifierPixels();}
    }
    private void DrawViewerMagnifier(CanvasControl sender,CanvasDrawEventArgs e)
    {
        e.DrawingSession.Clear(Microsoft.UI.Colors.Black);if(viewerGesture!=ViewerGesture.Magnifier||fitBitmap is null)return;
        double scale=PressZoomScale;var center=new Vector2((float)sender.ActualWidth/2,(float)sender.ActualHeight/2);
        e.DrawingSession.Transform=Matrix3x2.CreateRotation(rotation*(float)Math.PI/2,center);
        e.DrawingSession.DrawImage(fitBitmap,new Rect(center.X-lensNativeCenter.X*scale,center.Y-lensNativeCenter.Y*scale,sourceWidth*scale,sourceHeight*scale));
        foreach(var item in lensTiles){var size=item.Value.SizeInPixels;e.DrawingSession.DrawImage(item.Value,new Rect(center.X+(item.Key.X*1024-lensNativeCenter.X)*scale,center.Y+(item.Key.Y*1024-lensNativeCenter.Y)*scale,size.Width*scale,size.Height*scale));}
        e.DrawingSession.Transform=Matrix3x2.Identity;
    }
    private void BuildViewerContextMenu()
    {
        viewerContextMenu=new();
        viewerContextMenu.Opened+=(_,_)=>SetViewerMenuNotice(true);
        viewerContextMenu.Closed+=(_,_)=>SetViewerMenuNotice(false);
        foreach(var option in new[]{("上一张 · PageUp",ViewerAction.Previous),("下一张 · Space",ViewerAction.Next),("适屏 · B",ViewerAction.Fit),("100% · Ctrl+0",ViewerAction.Actual),("适合宽度 · Shift+W",ViewerAction.FitWidth),("适合高度 · Shift+H",ViewerAction.FitHeight),("自动适应 · Ctrl+Shift+K",ViewerAction.AutomaticSizing),("锁定缩放 · Ctrl+Shift+L",ViewerAction.LockSizing),("只读旋转 · R",ViewerAction.Rotate),("全屏 / 窗口查看 · F11",ViewerAction.ToggleFullScreen),("返回浏览列表",ViewerAction.ReturnBrowser)})
        {
            if(IsImageViewerAction(option.Item2)&&selected?.Kind!="image")continue;
            string label=selected?.Kind!="image"&&option.Item2==ViewerAction.Previous?"上一个文件 · PageUp":selected?.Kind!="image"&&option.Item2==ViewerAction.Next?"下一个文件 · PageDown":option.Item1;
            var item=new MenuFlyoutItem{Text=label};item.Click+=async(_,_)=>await RunViewerAction(option.Item2);viewerContextMenu.Items.Add(item);
        }
        viewerContextMenu.Items.Add(new MenuFlyoutSeparator());
        if(selected?.Kind=="image"){var settingsItem=new MenuFlyoutItem{Text="图片查看设置…"};settingsItem.Click+=ConfigureImageViewing;viewerContextMenu.Items.Add(settingsItem);}
        var open=new MenuFlyoutItem{Text=FileCommandLabels.ExternalOpen};open.Click+=ExternalOpen;viewerContextMenu.Items.Add(open);
        var copy=new MenuFlyoutItem{Text=FileCommandLabels.CopyPath};copy.Click+=CopyPath;viewerContextMenu.Items.Add(copy);var reveal=new MenuFlyoutItem{Text=FileCommandLabels.Reveal};reveal.Click+=Reveal;viewerContextMenu.Items.Add(reveal);
    }
    private static bool IsImageViewerAction(ViewerAction action)=>action is ViewerAction.Fit or ViewerAction.Actual or ViewerAction.FitWidth or ViewerAction.FitHeight or ViewerAction.Rotate or ViewerAction.AutomaticSizing or ViewerAction.LockSizing or ViewerAction.Slideshow;
    private void ShowViewerContextMenu(Point at){PreviewSurface.SetCursorHidden(false);viewerIdleTimer?.Stop();BuildViewerContextMenu();viewerContextMenu!.ShowAt(ImageCanvas,new FlyoutShowOptions{Position=at});}
    private void SetViewerWheelBehavior(string mode)
    {
        if(mode is not ("next" or "pan" or "zoom"))return;wheelBehavior=mode;
        if(viewerWheelSelector is not null){viewerPreferencesLoading=true;SelectTag(viewerWheelSelector,mode);viewerPreferencesLoading=false;}
        UpdateViewerInformation();_=SaveViewerPreferences();
    }
    private async Task RestoreViewerPreferences()
    {
        if(settings is null)return;
        try
        {
            if(await settings.Load<ViewerPreferences>("viewer.json") is not {} state)return;
            viewerPreferencesLoading=true;wheelBehavior=state.Wheel is "next" or "pan" or "zoom"?state.Wheel:"next";viewerSizing=state.Sizing=="locked"?ViewerSizing.Locked:ViewerSizing.Automatic;
            viewerLockedPhysicalScale=double.IsFinite(state.LockedPhysicalScale)?Math.Clamp(state.LockedPhysicalScale,.01,16):1;
            viewerPressZoom=(state.PressZoom??new PressZoomOptions()).Normalize();
            if(state.PressZoomVersion==0&&viewerPressZoom.Percent==100)viewerPressZoom=viewerPressZoom with{Percent=250};
            if(viewerWheelSelector is not null)SelectTag(viewerWheelSelector,wheelBehavior);
        }
        catch(Exception ex){ShowError(ex);}finally{viewerPreferencesLoading=false;}
    }
    private async Task SaveViewerPreferences()
    {
        if(settings is null||viewerPreferencesLoading)return;await viewerPreferencesGate.WaitAsync();
        try{await settings.Save("viewer.json",new ViewerPreferences(wheelBehavior,viewerSizing==ViewerSizing.Locked?"locked":"auto",viewerLockedPhysicalScale,viewerPressZoom,1));}
        catch(Exception ex){ShowError(ex);}finally{viewerPreferencesGate.Release();}
    }
    [DllImport("user32.dll")]private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
}
