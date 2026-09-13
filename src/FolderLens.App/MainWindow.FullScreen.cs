using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool fullScreen,wasMaximized,viewerPanelsVisible;
    private double browserOffset;
    private long browserEntryOrdinal;
    private string? browserAnchorPath;
    private string wheelBehavior="next";
    private int wheelRemainder;
    private Border? viewerTop,viewerBottom,viewerLeft,viewerRight;
    private TextBlock? viewerCaption,viewerInformation,viewerPosition;
    private ListView? viewerStrip;
    private ComboBox? viewerWheelSelector;
    private Button? viewerExternalPlayer;
    private long viewerModeRevision;
    private readonly List<(Button Button,ViewerAction Action)> viewerActionButtons=[];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? viewerIdleTimer;

    private void InitializeFullScreen()
    {
        viewerCaption=new TextBlock{FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(10,5,10,2)};
        viewerStrip=new ListView{Height=124,SelectionMode=ListViewSelectionMode.Single,IsItemClickEnabled=true,Padding=new Thickness(8,0,8,3)};
        viewerStrip.ItemsPanel=(ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");
        viewerStrip.ItemTemplate=(DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Width='108' Spacing='3'><Image Source='{Binding Thumbnail}' Height='76' Stretch='Uniform'/><TextBlock Text='{Binding Name}' FontSize='10' TextTrimming='CharacterEllipsis'/></StackPanel></DataTemplate>");
        ScrollViewer.SetHorizontalScrollBarVisibility(viewerStrip,ScrollBarVisibility.Auto);ScrollViewer.SetVerticalScrollBarVisibility(viewerStrip,ScrollBarVisibility.Disabled);
        viewerStrip.SelectionChanged+=SelectFile;viewerStrip.ContainerContentChanging+=ContainerChanged;
        var topContent=new StackPanel();topContent.Children.Add(viewerCaption);topContent.Children.Add(viewerStrip);viewerTop=ViewerPanel(topContent,HorizontalAlignment.Stretch,VerticalAlignment.Top);
        viewerPosition=new TextBlock{VerticalAlignment=VerticalAlignment.Center,MinWidth=100,FontSize=12};
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Padding=new Thickness(12,8,12,8)};
        AddViewerActionButton(controls,"◀ 上一张",ViewerAction.Previous);AddViewerActionButton(controls,"幻灯片",ViewerAction.Slideshow);AddViewerActionButton(controls,"下一张 ▶",ViewerAction.Next);viewerExternalPlayer=AddViewerButton(controls,"使用本地播放器打开",ExternalOpen);controls.Children.Add(viewerPosition);
        AddAnimationMirror(controls,AnimationButton,ToggleAnimation);AddAnimationMirror(controls,ReplayAnimationButton,ReplayAnimation);
        AddViewerActionButton(controls,"适屏",ViewerAction.Fit);AddViewerActionButton(controls,"100%",ViewerAction.Actual);AddViewerActionButton(controls,"适合宽度",ViewerAction.FitWidth);AddViewerActionButton(controls,"适合高度",ViewerAction.FitHeight);AddViewerActionButton(controls,"旋转",ViewerAction.Rotate);
        viewerWheelSelector=new ComboBox{MinWidth=148,SelectedIndex=-1};foreach(var option in new[]{("滚轮：切图 / 长图平移","next"),("滚轮：上下平移","pan"),("滚轮：缩放","zoom")})viewerWheelSelector.Items.Add(new ComboBoxItem{Content=option.Item1,Tag=option.Item2});viewerWheelSelector.SelectedIndex=0;
        viewerWheelSelector.SelectionChanged+=(_,_)=>{if(!viewerPreferencesLoading)SetViewerWheelBehavior(Tag(viewerWheelSelector));};controls.Children.Add(viewerWheelSelector);AddViewerButton(controls,"返回列表 · Esc",ExitFullScreen);
        viewerBottom=ViewerPanel(new ScrollViewer{Content=controls,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled},HorizontalAlignment.Stretch,VerticalAlignment.Bottom);
        viewerInformation=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=13,Width=270,Margin=new Thickness(16)};viewerRight=ViewerPanel(new ScrollViewer{Content=viewerInformation,MaxHeight=720},HorizontalAlignment.Right,VerticalAlignment.Center);
        var commands=new StackPanel{Spacing=8,Padding=new Thickness(12),Width=170};AddViewerActionButton(commands,"自动适应",ViewerAction.AutomaticSizing);AddViewerActionButton(commands,"锁定缩放比例",ViewerAction.LockSizing);AddViewerButton(commands,FileCommandLabels.CopyPath,CopyPath);AddViewerButton(commands,FileCommandLabels.CopyFileReference,CopyFileReference);AddViewerButton(commands,FileCommandLabels.Reveal,Reveal);AddViewerButton(commands,FileCommandLabels.ExternalOpen,ExternalOpen);AddViewerActionButton(commands,"窗口查看 · F11",ViewerAction.ToggleFullScreen);AddViewerButton(commands,"返回列表",ExitFullScreen);viewerLeft=ViewerPanel(commands,HorizontalAlignment.Left,VerticalAlignment.Center);
        Shell.PointerMoved+=ViewerPointerMoved;
        InitializeViewerInput();
        InitializeGroupNotice();
        viewerIdleTimer=DispatcherQueue.CreateTimer();viewerIdleTimer.Interval=TimeSpan.FromMilliseconds(1600);viewerIdleTimer.IsRepeating=false;
        viewerIdleTimer.Tick+=(_,_)=>{if(fullScreen&&!viewerPanelsVisible)PreviewSurface.SetCursorHidden(true);};
    }
    private Border ViewerPanel(UIElement content,HorizontalAlignment horizontal,VerticalAlignment vertical)
    {
        var border=new Border{Child=content,Style=(Style)Shell.Resources["ViewerPanelStyle"],HorizontalAlignment=horizontal,VerticalAlignment=vertical,Visibility=Visibility.Collapsed};
        Grid.SetRow(border,0);Grid.SetRowSpan(border,4);Shell.Children.Add(border);return border;
    }
    private static Button AddViewerButton(Panel panel,string label,RoutedEventHandler action){var button=new Button{Content=label};ToolTipService.SetToolTip(button,label);button.Click+=action;panel.Children.Add(button);return button;}
    private static void AddAnimationMirror(Panel panel,Button source,RoutedEventHandler action)
    {
        var button=new Button();button.SetBinding(Button.ContentProperty,new Microsoft.UI.Xaml.Data.Binding{Source=source,Path=new PropertyPath("Content")});button.SetBinding(UIElement.VisibilityProperty,new Microsoft.UI.Xaml.Data.Binding{Source=source,Path=new PropertyPath("Visibility")});button.Click+=action;panel.Children.Add(button);
    }
    private void AddViewerActionButton(Panel panel,string label,ViewerAction action)
    {
        var button=new Button{Content=label};ToolTipService.SetToolTip(button,label);
        button.Click+=async(_,_)=>await RunViewerAction(action);panel.Children.Add(button);viewerActionButtons.Add((button,action));
    }
    private void ViewerPointerMoved(object sender,PointerRoutedEventArgs e)
    {
        if(!fullScreen)return;PreviewSurface.SetCursorHidden(false);viewerIdleTimer?.Stop();var p=e.GetCurrentPoint(Shell).Position;
        bool top=p.Y<=5||(viewerTop!.Visibility==Visibility.Visible&&p.Y<=viewerTop.ActualHeight+6);
        bool bottom=p.Y>=Shell.ActualHeight-5||(viewerBottom!.Visibility==Visibility.Visible&&p.Y>=Shell.ActualHeight-viewerBottom.ActualHeight-6);
        bool left=p.X<=5||(viewerLeft!.Visibility==Visibility.Visible&&p.X<=viewerLeft.ActualWidth+6&&Math.Abs(p.Y-Shell.ActualHeight/2)<=viewerLeft.ActualHeight/2+6);
        bool right=p.X>=Shell.ActualWidth-5||(viewerRight!.Visibility==Visibility.Visible&&p.X>=Shell.ActualWidth-viewerRight.ActualWidth-6&&Math.Abs(p.Y-Shell.ActualHeight/2)<=viewerRight.ActualHeight/2+6);
        viewerTop!.Visibility=top?Visibility.Visible:Visibility.Collapsed;viewerBottom!.Visibility=bottom?Visibility.Visible:Visibility.Collapsed;viewerLeft!.Visibility=left?Visibility.Visible:Visibility.Collapsed;viewerRight!.Visibility=right?Visibility.Visible:Visibility.Collapsed;
        if(top&&!viewerPanelsVisible&&selected is not null&&viewerStrip is not null){viewerStrip.SelectedItem=selected;viewerStrip.ScrollIntoView(selected);}
        viewerPanelsVisible=top||bottom||left||right;if(!viewerPanelsVisible)viewerIdleTimer?.Start();
    }
    private void UpdateViewerInformation()
    {
        UpdateGroupNotice();
        PreviewFilePath.Text=selected?.RelativePath??"";
        PreviewFilePath.Visibility=selected is null?Visibility.Collapsed:Visibility.Visible;
        bool video=selected?.Kind=="video",picture=selected?.Kind=="image";
        PreviewFit.Visibility=PreviewActual.Visibility=PreviewRotate.Visibility=video?Visibility.Collapsed:Visibility.Visible;
        PreviewExternalPlayer.Visibility=video?Visibility.Visible:Visibility.Collapsed;
        foreach(var item in viewerActionButtons)
        {
            item.Button.Visibility=IsImageViewerAction(item.Action)
                ?picture?Visibility.Visible:Visibility.Collapsed:Visibility.Visible;
            if(item.Action is ViewerAction.Previous or ViewerAction.Next)
            {
                string label=item.Action==ViewerAction.Previous?(picture?"◀ 上一张":"◀ 上一个文件"):(picture?"下一张 ▶":"下一个文件 ▶");
                item.Button.Content=label;ToolTipService.SetToolTip(item.Button,label);
            }
        }
        if(viewerExternalPlayer is not null)viewerExternalPlayer.Visibility=video?Visibility.Visible:Visibility.Collapsed;
        if(viewerWheelSelector is not null)viewerWheelSelector.Visibility=video?Visibility.Collapsed:Visibility.Visible;
        if(selected is null){if(viewerCaption is not null)viewerCaption.Text="未选择文件";if(viewerPosition is not null)viewerPosition.Text="";if(viewerInformation is not null)viewerInformation.Text="请选择当前结果中的文件。";return;}string position=$"{selected.Ordinal+1:N0} / {resultHandle?.Count??0:N0}";
        string groupCaption=selected.Item?.Group is {} group?$"    文件夹：{(group.RelativePath.Length==0?"本目录文件":group.RelativePath)}    {FileRow.FormatBytes(group.Bytes)}{(group.ScanState=="ready"?"":" · 部分统计")}":"";
        if(viewerCaption is not null)viewerCaption.Text=$"{position}    {selected.Name}    {selected.Detail}{groupCaption}";
        if(viewerPosition is not null)viewerPosition.Text=position;
        string help=video?"视频封面预览\n滚轮 / ← → / PageUp、PageDown：切换文件\n双击：使用本地播放器打开\nEsc：返回列表\nF11：全屏 / 窗口查看"
            :$"滚轮：{(EffectiveViewerWheelBehavior()=="next"?"切换图片":EffectiveViewerWheelBehavior()=="pan"?"平移阅读":"缩放")}\n短击：100% / 适屏\n按住：{(viewerPressZoom.UsesWholeImage(immersive||fullScreen)?"整图临时放大":"局部放大镜")} · {viewerPressZoom.Percent}%\n放大后拖动：平移\n双击 / Esc：返回列表\nF11：全屏 / 窗口查看\n缩放：{(viewerSizing==ViewerSizing.Locked?"锁定比例":"自动适应")}";
        string exif=selected.Kind=="image"&&selectedProperties?.EntryId==selected.Item?.EntryId&&selectedProperties?.Version==selected.Item?.Version?FormatViewerExif(selectedProperties?.Details):"";
        if(viewerInformation is not null)viewerInformation.Text=$"{selected.Name}\n\n{selected.Detail}\n\n{selected.RelativePath}\n\n{QualityLabel.Text}{(exif.Length>0?"\n\n"+exif:"")}\n\n{help}";
    }
    private (Thickness Padding,Thickness Border)? previewChromeBeforeFullScreen;
    private void SetFullScreenChrome(bool enabled)
    {
        MainMenu.Visibility=AddressToolbar.Visibility=Status.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        PreviewHeader.Visibility=PreviewActions.Visibility=PreviewExtras.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        if(enabled)
        {
            previewChromeBeforeFullScreen??=(PreviewPane.Padding,PreviewPane.BorderThickness);
            PreviewPane.Padding=PreviewPane.BorderThickness=new Thickness(0);
        }
        else if(previewChromeBeforeFullScreen is {} previous)
        {
            PreviewPane.Padding=previous.Padding;PreviewPane.BorderThickness=previous.Border;previewChromeBeforeFullScreen=null;
        }
        foreach(var panel in new[]{viewerTop,viewerBottom,viewerLeft,viewerRight})if(panel is not null)panel.Visibility=Visibility.Collapsed;
        PreviewSurface.SetCursorHidden(false);viewerPanelsVisible=false;
        if(enabled){UpdateViewerInformation();viewerIdleTimer?.Start();}else{viewerIdleTimer?.Stop();UpdateGroupNotice();}
    }
    private async Task EnterFullScreen()
    {
        if(fullScreen)return;long revision=++viewerModeRevision;
        if(selected is null&&results?.Count>0){if(DetailsMode.IsChecked==true)FilesList.SelectedIndex=0;else FilesGrid.SelectedIndex=0;}
        if(selected is null)return;
        wasMaximized=AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p&&p.State==Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        fullScreen=true;var entering=SetImmersive(true);SetFullScreenChrome(true);AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);ImageCanvas.Focus(FocusState.Programmatic);
        await entering;if(revision!=viewerModeRevision||!fullScreen)return;ApplyViewerSizing();
        if(selected is not null&&!previewLoading&&zoom==0)try{await EnsureFitResolution(selected,selection,selectionStop.Token);}catch(OperationCanceledException){}catch(Exception ex){ShowPreviewError(ex);}
    }
    private async void ExitFullScreen(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.ReturnBrowser);
    private async Task LeaveFullScreen(bool toBrowser=true)
    {
        ++viewerModeRevision;ResetViewerGesture();slideShow=false;slideTimer?.Stop();fullScreen=false;SetFullScreenChrome(false);
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        if(wasMaximized&&AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)p.Maximize();
        if(toBrowser)await SetImmersive(false);else{await SetImmersive(true);ImageCanvas.Focus(FocusState.Programmatic);ApplyViewerSizing();}
    }
    private void CaptureBrowserPosition()
    {
        browserEntryOrdinal=selected?.Ordinal??-1;browserOffset=FindScrollViewer(DetailsMode.IsChecked==true?FilesList:FilesGrid)?.VerticalOffset??0;
        browserAnchorPath=VisibleBrowserPath();
    }
    private void RestoreBrowserPosition()
    {
        var list=DetailsMode.IsChecked==true?(ListViewBase)FilesList:FilesGrid;list.UpdateLayout();
        if(selected is not null){RevealBrowserRow(selected);list.SelectedItem=selected;}
        if(selected is not null&&selected.Ordinal==browserEntryOrdinal)FindScrollViewer(list)?.ChangeView(null,browserOffset,null,true);
        else if(selected is not null)list.ScrollIntoView(selected);
        list.Focus(FocusState.Programmatic);
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if(parent is ScrollViewer viewer)return viewer;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindScrollViewer(VisualTreeHelper.GetChild(parent,i));if(found is not null)return found;}return null;
    }
    private async void FitWidth(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.FitWidth);
    private void ClampPan()
    {
        if(sourceWidth<=0||sourceHeight<=0||Shell.XamlRoot is null)return;
        double scale=EffectiveScale(),width=(rotation%2==0?sourceWidth:sourceHeight)*scale,height=(rotation%2==0?sourceHeight:sourceWidth)*scale;
        var screen=Vector2.TransformNormal(pan,Matrix3x2.CreateRotation(rotation*(float)Math.PI/2));
        screen.X=(float)Math.Clamp(screen.X,-Math.Max(0,(width-ImageCanvas.ActualWidth)/2),Math.Max(0,(width-ImageCanvas.ActualWidth)/2));
        screen.Y=(float)Math.Clamp(screen.Y,-Math.Max(0,(height-ImageCanvas.ActualHeight)/2),Math.Max(0,(height-ImageCanvas.ActualHeight)/2));
        pan=Vector2.TransformNormal(screen,Matrix3x2.CreateRotation(-rotation*(float)Math.PI/2));
    }
}
