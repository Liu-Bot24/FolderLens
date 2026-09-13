using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFitLock(string directory,Dictionary<string,object> report)
    {
        root=directory;selected=new FileRow(0);selected.Fill(new(0,"fit-lock",1,"A/image-00.png","",0,null,"image"));
        fitBitmap=await CanvasBitmap.LoadAsync(ImageCanvas,Path.Combine(directory,"A","image-00.png"));
        sourceWidth=64;sourceHeight=48;previewLoading=false;offlinePreview=true;
        await SetImmersive(true);
        var originalFit=fitBitmap;
        await EnsureFitResolution(selected,selection,selectionStop.Token);
        if(!ReferenceEquals(fitBitmap,originalFit))throw new InvalidOperationException("原图像素已完整加载，小图放大不应再次解码");
        var failures=new List<string>();
        void Check(bool condition,string message){if(!condition)failures.Add(message);}
        PreparePreview();Check(viewerLockToggle!.IsEnabled==false,"图片加载时锁定按钮未禁用");
        FinishPreview();Check(viewerLockToggle.IsEnabled,"图片加载完成后锁定按钮未恢复可用");
        foreach(var shape in new[]{(64d,48d,0),(48d,128d,0),(8000d,4000d,0),(64d,48d,1)})
        {
            (sourceWidth,sourceHeight,rotation)=shape;await RunViewerAction(ViewerAction.Fit);
            double width=rotation%2==0?sourceWidth:sourceHeight,height=rotation%2==0?sourceHeight:sourceWidth;
            double expected=Math.Min(ImageCanvas.ActualWidth/width,ImageCanvas.ActualHeight/height);
            Check(Math.Abs(EffectiveScale()-expected)<.00001,$"适应屏幕比例错误：{shape}, expected={expected}, actual={EffectiveScale()}");
        }
        BuildViewerContextMenu();
        Check(!viewerContextMenu!.Items.OfType<MenuFlyoutItem>().Any(x=>x.Text.Contains("自动适应")),"右键仍包含自动适应");
        var toggle=viewerContextMenu.Items.OfType<ToggleMenuFlyoutItem>().SingleOrDefault(x=>x.Text.Contains("锁定缩放"));
        Check(toggle is not null,"锁定缩放不是勾选开关");
        if(toggle is not null)
        {
            sourceWidth=64;sourceHeight=48;rotation=0;viewerSizing=ViewerSizing.Automatic;
            await RunViewerAction(ViewerAction.Fit);double fitted=EffectiveScale();
            var provider=(IToggleProvider)new ToggleMenuFlyoutItemAutomationPeer(toggle).GetPattern(PatternInterface.Toggle);
            provider.Toggle();await WaitUntil(()=>viewerSizing==ViewerSizing.Locked,TimeSpan.FromSeconds(3));
            Check(toggle.IsChecked&&Math.Abs(EffectiveScale()-fitted)<.00001,"勾选时改变了当前画面");
            sourceWidth=128;sourceHeight=96;selection++;ApplyViewerSizing();
            Check(Math.Abs(EffectiveScale()-fitted)<.00001,"切图没有保留锁定比例");
            await SaveViewerPreferences();viewerSizing=ViewerSizing.Automatic;await RestoreViewerPreferences();
            Check(viewerSizing==ViewerSizing.Locked,"锁定设置没有保存恢复");
            Check(Math.Abs(viewerLockedPhysicalScale-fitted*Shell.XamlRoot.RasterizationScale)<.00001,"小图适应屏幕后锁定的倍率在恢复时被截断");
            BuildViewerContextMenu();toggle=viewerContextMenu!.Items.OfType<ToggleMenuFlyoutItem>().Single();
            Check(toggle.IsChecked,"重新打开菜单未显示勾选状态");
            provider=(IToggleProvider)new ToggleMenuFlyoutItemAutomationPeer(toggle).GetPattern(PatternInterface.Toggle);
            provider.Toggle();await WaitUntil(()=>viewerSizing==ViewerSizing.Automatic,TimeSpan.FromSeconds(3));
            Check(!toggle.IsChecked&&Math.Abs(EffectiveScale()-fitted)<.00001,"取消锁定不应突然改变当前画面");
            selection++;ApplyViewerSizing();
            Check(Math.Abs(EffectiveScale()-Math.Min(ImageCanvas.ActualWidth/sourceWidth,ImageCanvas.ActualHeight/sourceHeight))<.00001,"取消锁定后下一张未适应屏幕");
            await RunViewerAction(ViewerAction.LockSizing);BuildViewerContextMenu();
            Check(viewerContextMenu!.Items.OfType<ToggleMenuFlyoutItem>().Single().IsChecked,"快捷键与菜单状态不同步");
            await RunViewerAction(ViewerAction.LockSizing);
            var sideProvider=(IToggleProvider)new ToggleButtonAutomationPeer(viewerLockToggle!).GetPattern(PatternInterface.Toggle);
            sideProvider.Toggle();await WaitUntil(()=>viewerSizing==ViewerSizing.Locked,TimeSpan.FromSeconds(3));
            BuildViewerContextMenu();Check(viewerLockToggle!.IsChecked==true&&viewerContextMenu!.Items.OfType<ToggleMenuFlyoutItem>().Single().IsChecked,"左侧按下状态与右键勾选不同步");
            sideProvider.Toggle();await WaitUntil(()=>viewerSizing==ViewerSizing.Automatic,TimeSpan.FromSeconds(3));
            Check(viewerLockToggle.IsChecked==false,"左侧再次按下没有解除锁定");
        }
        double beforeQuickChange=EffectiveScale();string mode=viewerPressZoom.LargeViewMode;
        viewerPressZoomSelector!.SelectedItem=viewerPressZoomSelector.Items.OfType<ComboBoxItem>().Single(x=>x.Tag is double value&&value==400);
        await WaitUntil(()=>viewerPressZoom.Percent==400,TimeSpan.FromSeconds(3));
        Check(viewerPressZoom.LargeViewMode==mode&&EffectiveScale()==beforeQuickChange,"切换长按倍率改变了放大模式或当前图片缩放");
        await SaveViewerPreferences();viewerPressZoom=viewerPressZoom with{Percent=250};await RestoreViewerPreferences();
        Check(viewerPressZoom.Percent==400&&viewerPressZoomSelector.SelectedItem is ComboBoxItem{Tag:400d},"长按倍率保存恢复不同步");
        viewerPressZoom=viewerPressZoom with{Percent=375};UpdateViewerInformation();
        Check(viewerPressZoomSelector.SelectedItem is ComboBoxItem{Tag:375d},"设置中的自定义倍率未反映到快捷选择器");
        report["failures"]=failures;report["viewport"]=new[]{ImageCanvas.ActualWidth,ImageCanvas.ActualHeight};
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        report["status"]="PASS";
    }
}
