using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyTheme(string source,Dictionary<string,object> report)
    {
        var initialAppearance=new FolderLens.Core.AppearancePreferences(Environment.GetCommandLineArgs().Contains("--verify-soft")?"soft":"native");
        ApplyAppearance(initialAppearance);
        report["appearance"]=appearance.Style;
        var expected=Application.Current.RequestedTheme==ApplicationTheme.Dark?ElementTheme.Dark:ElementTheme.Light;
        report["applicationTheme"]=expected.ToString();report["shellTheme"]=Shell.ActualTheme.ToString();
        if(Shell.RequestedTheme!=ElementTheme.Default||Shell.ActualTheme!=expected)throw new InvalidOperationException("主窗口没有继承应用/系统主题。");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        // The watcher emits an initial reconnect reconciliation after its first tick.
        // Let that real publication drain before asserting appearance-only stability.
        var settling=System.Diagnostics.Stopwatch.StartNew();
        var quiet=System.Diagnostics.Stopwatch.StartNew();long settledRequest=queryRequest;
        while(quiet.Elapsed<TimeSpan.FromMilliseconds(1500))
        {
            if(settling.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("主题测试的初始扫描未稳定。");
            await Task.Delay(50);
            if(queryRequest!=settledRequest||queryBusy||reconcilePending||scanTask is {IsCompleted:false})
            {settledRequest=queryRequest;quiet.Restart();}
        }
        if(results is null||results.Count==0)throw new InvalidOperationException("主题测试没有加载浏览文件。");
        await SelectBrowserOrdinal(results,0,lifetime.Token);
        DirectoryScopePanel.Visibility=Visibility.Visible;DirectoryScopeLabel.Text="当前文件夹范围";
        ActiveFilterSummary.Visibility=Visibility.Visible;ActiveFilterSummary.Text="已应用图片筛选";
        Shell.UpdateLayout();await Task.Delay(100);
        static double Luminance(Windows.UI.Color color)
        {
            static double Channel(byte b){double x=b/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
            return .2126*Channel(color.R)+.7152*Channel(color.G)+.0722*Channel(color.B);
        }
        double Contrast(Brush text,Brush background)
        {
            var ink=((SolidColorBrush)text).Color;var paper=((SolidColorBrush)background).Color;
            if(paper.A!=255)
            {
                var baseColor=((SolidColorBrush)BrowserPane.Background).Color;double opacity=paper.A/255d;
                paper=Microsoft.UI.ColorHelper.FromArgb(255,(byte)Math.Round(paper.R*opacity+baseColor.R*(1-opacity)),(byte)Math.Round(paper.G*opacity+baseColor.G*(1-opacity)),(byte)Math.Round(paper.B*opacity+baseColor.B*(1-opacity)));
            }
            double alpha=ink.A/255d;
            var visible=Microsoft.UI.ColorHelper.FromArgb(255,(byte)Math.Round(ink.R*alpha+paper.R*(1-alpha)),(byte)Math.Round(ink.G*alpha+paper.G*(1-alpha)),(byte)Math.Round(ink.B*alpha+paper.B*(1-alpha)));
            double a=Luminance(visible),b=Luminance(paper);
            return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
        }
        double content=Contrast(ResultSummary.Foreground,BrowserPane.Background),scope=Contrast(DirectoryScopeLabel.Foreground,DirectoryScopePanel.Background),filter=Contrast(ActiveFilterSummary.Foreground,BrowserPane.Background);
        report["textContrast"]=new{content,scope,filter};
        if(content<4.5||scope<4.5||filter<4.5)throw new InvalidOperationException("主题文字与背景对比度不足。");
        viewerCaption!.Text="图片名称与路径";viewerInformation!.Text="图片信息 · 1920 × 1080";
        viewerTop!.Visibility=viewerRight!.Visibility=Visibility.Visible;Shell.UpdateLayout();
        double caption=Contrast(viewerCaption.Foreground,viewerTop.Background),information=Contrast(viewerInformation.Foreground,viewerRight.Background);
        report["viewerContrast"]=new{caption,information};
        if(caption<4.5||information<4.5)throw new InvalidOperationException("查看浮层文字与背景对比度不足。");
        var oldPanelColor=((SolidColorBrush)viewerTop.Background).Color;
        var originalSource=FilesGrid.ItemsSource;
        var other=expected==ElementTheme.Dark?ElementTheme.Light:ElementTheme.Dark;
        var before=((SolidColorBrush)BrowserPane.Background).Color;
        Shell.RequestedTheme=other;Shell.UpdateLayout();await Task.Delay(50);
        if(Shell.ActualTheme!=other||((SolidColorBrush)BrowserPane.Background).Color==before)throw new InvalidOperationException("运行时主题资源没有更新。");
        if(((SolidColorBrush)viewerTop.Background).Color==oldPanelColor||Contrast(viewerCaption.Foreground,viewerTop.Background)<4.5||Contrast(viewerInformation.Foreground,viewerRight.Background)<4.5)throw new InvalidOperationException("查看浮层没有正确切换主题。");
        Shell.RequestedTheme=ElementTheme.Default;Shell.UpdateLayout();await Task.Delay(50);
        if(Shell.ActualTheme!=expected||!ReferenceEquals(originalSource,FilesGrid.ItemsSource))throw new InvalidOperationException("恢复系统主题改变了浏览源或主题。");
        void CheckStructuralLines()
        {
            bool soft=appearance.Style=="soft";
            foreach(var panel in new[]{BrowserToolbar,BrowserStatusBar,PreviewPane})
                if((((SolidColorBrush)panel.BorderBrush).Color.A==0)!=soft)throw new InvalidOperationException("分区线没有随外观正确切换。");
            foreach(var divider in new Microsoft.UI.Xaml.Controls.Border[]{(Microsoft.UI.Xaml.Controls.Border)PaneDivider.Children[0],(Microsoft.UI.Xaml.Controls.Border)PreviewHeightDivider.Children[0]})
                if(divider.Opacity!=(soft?0:1))throw new InvalidOperationException("分栏线没有随外观正确切换。");
            if(!PaneDivider.IsHitTestVisible||!PreviewHeightDivider.IsHitTestVisible||PaneDivider.ActualWidth<=0||PreviewHeightDivider.ActualHeight<=0)
                throw new InvalidOperationException("隐藏分栏线破坏了拖动区域。");
        }
        CheckStructuralLines();
        var originalResult=results;var originalEpoch=epoch;var originalRoot=root;var originalSelection=selected;
        var originalGeneration=generation;var originalQueryRequest=queryRequest;var originalFilter=System.Text.Json.JsonSerializer.Serialize(CurrentFilter());
        var initialRadius=BrowserPane.CornerRadius;
        var initialColor=((SolidColorBrush)BrowserPane.Background).Color;
        var alternateAppearance=new FolderLens.Core.AppearancePreferences(appearance.Style=="soft"?"native":"soft");
        for(int pass=0;pass<3;pass++)
        {
            ApplyAppearance(alternateAppearance);Shell.UpdateLayout();await Task.Delay(30);CheckStructuralLines();
            if(BrowserPane.CornerRadius==initialRadius)throw new InvalidOperationException("切换外观没有更新布局资源。");
            if(Contrast(ResultSummary.Foreground,BrowserPane.Background)<4.5||Contrast(ActiveFilterSummary.Foreground,BrowserPane.Background)<4.5)throw new InvalidOperationException("外观切换后文字对比度不足。");
            ApplyAppearance(initialAppearance);Shell.UpdateLayout();await Task.Delay(30);CheckStructuralLines();
            if(BrowserPane.CornerRadius!=initialRadius||((SolidColorBrush)BrowserPane.Background).Color!=initialColor)throw new InvalidOperationException("切回原外观后资源未恢复。");
        }
        report["appearanceState"]=new{sameResults=ReferenceEquals(originalResult,results),sameItems=ReferenceEquals(originalSource,FilesGrid.ItemsSource),sameEpoch=epoch==originalEpoch,sameRoot=root==originalRoot,sameSelection=ReferenceEquals(originalSelection,selected),originalGeneration,generation,originalQueryRequest,queryRequest,sameFilter=originalFilter==System.Text.Json.JsonSerializer.Serialize(CurrentFilter()),queryBusy};
        if(!ReferenceEquals(originalResult,results)||!ReferenceEquals(originalSource,FilesGrid.ItemsSource)||epoch!=originalEpoch||root!=originalRoot||!ReferenceEquals(originalSelection,selected))throw new InvalidOperationException("外观切换改变了浏览状态。");
        if(Shell.RequestedTheme!=ElementTheme.Default)throw new InvalidOperationException("外观切换覆盖了跟随系统设置。");
        await settings!.Save("appearance.json",initialAppearance);
        ApplyAppearance(alternateAppearance);await RestoreAppearance();
        if(appearance!=initialAppearance||SoftAppearance.IsChecked!=(initialAppearance.Style=="soft")||NativeAppearance.IsChecked!=(initialAppearance.Style=="native"))throw new InvalidOperationException("外观选择未保存或菜单状态错误。");
        report["runtimeAppearanceRoundTrips"]=3;report["appearancePersisted"]=true;report["browsingStatePreserved"]=true;
        viewerTop.Visibility=viewerRight.Visibility=Visibility.Collapsed;Shell.UpdateLayout();
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(Shell);
        using(var file=File.Create(Path.Combine(dataDirectory,"theme.png")))
        using(var stream=file.AsRandomAccessStream())
        {
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        }
        Task help=ShowLocalHelp();
        try
        {
            await WaitUntil(()=>localHelpDialog is {IsLoaded:true},TimeSpan.FromSeconds(3));
            if(localHelpDialog!.ActualTheme!=expected)throw new InvalidOperationException("帮助弹层没有继承应用主题。");
            report["helpTheme"]=localHelpDialog.ActualTheme.ToString();
        }
        finally{localHelpDialog?.Hide();await help;}
        report["systemSettingsChanged"]=false;report["status"]="PASS";
    }
}
