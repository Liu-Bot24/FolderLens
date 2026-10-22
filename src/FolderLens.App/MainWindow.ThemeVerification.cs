using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Color=Windows.UI.Color;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyTheme(string source,Dictionary<string,object> report)
    {
        string[] arguments=Environment.GetCommandLineArgs();
        var initialAppearance=new FolderLens.Core.AppearancePreferences(arguments.Contains("--verify-glass")?"glass":arguments.Contains("--verify-soft")?"soft":"native");
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
        static double Luminance(Color color)
        {
            static double Channel(byte b){double x=b/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
            return .2126*Channel(color.R)+.7152*Channel(color.G)+.0722*Channel(color.B);
        }
        static Color Over(Color top,Color bottom)
        {
            double alpha=top.A/255d;
            return Microsoft.UI.ColorHelper.FromArgb(255,(byte)Math.Round(top.R*alpha+bottom.R*(1-alpha)),(byte)Math.Round(top.G*alpha+bottom.G*(1-alpha)),(byte)Math.Round(top.B*alpha+bottom.B*(1-alpha)));
        }
        // Every surface a background can show over the given backdrops. Acrylic shows
        // its fallback when unavailable, otherwise at least its tint over the backdrop.
        static List<Color> Papers(Brush brush,IReadOnlyList<Color> under)=>brush switch
        {
            SolidColorBrush solid=>under.Select(color=>Over(solid.Color,color)).ToList(),
            AcrylicBrush acrylic=>under.Select(color=>Over(acrylic.FallbackColor,color))
                .Concat(under.Select(color=>Over(Microsoft.UI.ColorHelper.FromArgb((byte)Math.Round(acrylic.TintOpacity*255),acrylic.TintColor.R,acrylic.TintColor.G,acrylic.TintColor.B),color))).ToList(),
            _=>throw new InvalidOperationException("主题测试不支持的背景画刷："+brush.GetType().Name)
        };
        static Color Identity(Brush brush)=>brush switch
        {
            SolidColorBrush solid=>solid.Color,AcrylicBrush acrylic=>acrylic.FallbackColor,
            _=>throw new InvalidOperationException("主题测试不支持的背景画刷："+brush.GetType().Name)
        };
        static byte Alpha(Brush brush)=>brush switch
        {
            SolidColorBrush solid=>solid.Color.A,GradientBrush gradient=>gradient.GradientStops.Max(stop=>stop.Color.A),
            _=>throw new InvalidOperationException("主题测试不支持的线条画刷："+brush.GetType().Name)
        };
        static double Contrast(Brush text,IReadOnlyList<Color> papers)
        {
            var ink=((SolidColorBrush)text).Color;
            return papers.Min(paper=>{double a=Luminance(Over(ink,paper)),b=Luminance(paper);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);});
        }
        // Behind the panes: the window colour plus, when shown, the strongest aurora tint.
        List<Color> Backdrop()
        {
            var chrome=((SolidColorBrush)Shell.Background).Color;var colors=new List<Color>{chrome};
            var aurora=((RadialGradientBrush)((Microsoft.UI.Xaml.Shapes.Rectangle)AppearanceBackdrop.Children[0]).Fill).GradientStops[0].Color;
            if(AppearanceBackdrop.Opacity>0)colors.Add(Over(aurora,chrome));
            return colors;
        }
        List<Color> Pane()=>Papers(BrowserPane.Background,Backdrop());
        // Full-screen overlays float over an arbitrary photo.
        var photo=new[]{Microsoft.UI.Colors.White,Microsoft.UI.Colors.Black};
        bool highContrast=accessibility.HighContrast;
        double content=Contrast(ResultSummary.Foreground,Pane()),scope=Contrast(DirectoryScopeLabel.Foreground,Papers(DirectoryScopePanel.Background,Pane())),filter=Contrast(ActiveFilterSummary.Foreground,Pane());
        report["textContrast"]=new{content,scope,filter};
        if(content<4.5||scope<4.5||filter<4.5)throw new InvalidOperationException("主题文字与背景对比度不足。");
        if(appearance.Style is "soft" or "glass")
        {
            if(RootPath.CornerRadius!=Search.CornerRadius||Search.CornerRadius.TopLeft<=0||Category.CornerRadius!=Search.CornerRadius)
                throw new InvalidOperationException("主题输入控件圆角不一致。");
            var pathBrush=(Brush)Application.Current.Resources["AppearancePathBrush"];
            double pathContrast=Contrast(pathBrush,Pane());
            if(pathContrast<4.5)throw new InvalidOperationException("路径文字对比度不足。");
            var palette=Application.Current.Resources.MergedDictionaries[1].ThemeDictionaries[expected.ToString()] as ResourceDictionary;
            var accent=((SolidColorBrush)palette!["AccentFillColorDefaultBrush"]).Color;
            foreach(string key in new[]{"ToggleButtonBackgroundChecked","SliderThumbBackground","SliderTrackValueFill","TreeViewItemSelectionIndicatorForeground","GridViewItemSelectedBorderBrush"})
                if(((SolidColorBrush)palette[key]).Color!=accent)throw new InvalidOperationException("控件强调色不一致："+key);
            double accentText=Contrast((Brush)palette["TextOnAccentFillColorPrimaryBrush"],[accent]);
            if(accentText<4.5)throw new InvalidOperationException("选中按钮文字对比度不足。");
            report["styledControls"]=new{pathContrast,accentText,inputRadius=Search.CornerRadius.TopLeft,previewButtonHeight=PreviewFit.ActualHeight};
        }
        void CheckOverlayAppearance()
        {
            bool glass=GlassAppearanceActive;
            foreach(var panel in new[]{viewerTop!,viewerBottom!,viewerLeft!,viewerRight!})
                if(panel.RequestedTheme!=(glass?ElementTheme.Dark:ElementTheme.Default)||!panel.Margin.Equals(Application.Current.Resources["ViewerOverlayMargin"]))
                    throw new InvalidOperationException("查看浮层的主题或边距没有随外观切换。");
            if(viewerBottom!.HorizontalAlignment!=(glass?HorizontalAlignment.Center:HorizontalAlignment.Stretch))throw new InvalidOperationException("底部浮层对齐方式没有随外观切换。");
            if(!ReferenceEquals(VideoPosterPlay.Style,Application.Current.Resources["VideoPosterPlayStyle"]))throw new InvalidOperationException("视频播放按钮样式没有随外观切换。");
            if(AppearanceBackdrop.Opacity!=(glass&&!highContrast?1:0))throw new InvalidOperationException("背景装饰层没有随外观切换。");
            if(!glass||highContrast)return;
            foreach(var pane in new Panel[]{TreePane,PreviewPane,BrowserPane})
                if(pane.Background is not AcrylicBrush)throw new InvalidOperationException("琉璃面板没有使用亚克力背景。");
            var surface=((SolidColorBrush)PreviewSurface.Background).Color;
            if(Math.Abs(surface.R-surface.G)>3||Math.Abs(surface.G-surface.B)>3)throw new InvalidOperationException("图片底色不是中性灰，会影响看片颜色。");
        }
        CheckOverlayAppearance();
        if(GlassAppearanceActive)
        {
            SetViewerInformation("标题.jpg",["2160 × 3840 · JPG","相机：Fixture Camera\n镜头：Fixture Lens","滚轮：切换图片"]);
            string text=viewerInformation!.Text;
            bool rich=highContrast||viewerInformation.Inlines.Count>1;
            report["glassInformation"]=new{rich,text};
            if(!rich||!text.Contains("标题.jpg")||!text.Contains("Fixture Camera")||!text.Contains("Fixture Lens")||!text.Contains("切换图片"))
                throw new InvalidOperationException("琉璃信息面板丢失了内容。");
        }
        viewerCaption!.Text="图片名称与路径";viewerInformation!.Text="图片信息 · 1920 × 1080";
        viewerTop!.Visibility=viewerRight!.Visibility=Visibility.Visible;Shell.UpdateLayout();
        double caption=Contrast(viewerCaption.Foreground,Papers(viewerTop.Background,photo)),information=Contrast(viewerInformation.Foreground,Papers(viewerRight.Background,photo));
        report["viewerContrast"]=new{caption,information};
        if(caption<4.5||information<4.5)throw new InvalidOperationException("查看浮层文字与背景对比度不足。");
        var oldPanelColor=Identity(viewerTop.Background);
        var originalSource=FilesGrid.ItemsSource;
        var other=expected==ElementTheme.Dark?ElementTheme.Light:ElementTheme.Dark;
        var before=Identity(BrowserPane.Background);
        Shell.RequestedTheme=other;Shell.UpdateLayout();await Task.Delay(50);
        if(Shell.ActualTheme!=other||Identity(BrowserPane.Background)==before)throw new InvalidOperationException("运行时主题资源没有更新。");
        // Glass overlays stay dark over photos in both app themes; others follow the app.
        bool overlayFollowsTheme=!GlassAppearanceActive;
        if((Identity(viewerTop.Background)!=oldPanelColor)!=overlayFollowsTheme||Contrast(viewerCaption.Foreground,Papers(viewerTop.Background,photo))<4.5||Contrast(viewerInformation.Foreground,Papers(viewerRight.Background,photo))<4.5)throw new InvalidOperationException("查看浮层没有正确切换主题。");
        Shell.RequestedTheme=ElementTheme.Default;Shell.UpdateLayout();await Task.Delay(50);
        if(Shell.ActualTheme!=expected||!ReferenceEquals(originalSource,FilesGrid.ItemsSource))throw new InvalidOperationException("恢复系统主题改变了浏览源或主题。");
        void CheckStructuralLines()
        {
            bool hidden=appearance.Style=="soft";
            foreach(var panel in new[]{BrowserToolbar,BrowserStatusBar})
                if((Alpha(panel.BorderBrush)==0)!=hidden)throw new InvalidOperationException("分区线没有随外观正确切换。");
            if(GlassAppearanceActive&&!highContrast)
            {
                if(PreviewPane.BorderBrush is not LinearGradientBrush||PreviewPane.BorderThickness!=new Thickness(1)||TreePane.BorderThickness!=new Thickness(1)||BrowserPane.BorderThickness!=new Thickness(1))
                    throw new InvalidOperationException("琉璃面板的高光边没有正确应用。");
            }
            else if((Alpha(PreviewPane.BorderBrush)==0)!=hidden)throw new InvalidOperationException("分区线没有随外观正确切换。");
            foreach(var divider in new Microsoft.UI.Xaml.Controls.Border[]{(Microsoft.UI.Xaml.Controls.Border)PaneDivider.Children[0],(Microsoft.UI.Xaml.Controls.Border)PreviewHeightDivider.Children[0]})
                if(divider.Opacity!=(appearance.Style=="native"||highContrast?1:0))throw new InvalidOperationException("分栏线没有随外观正确切换。");
            if(!PaneDivider.IsHitTestVisible||!PreviewHeightDivider.IsHitTestVisible||PaneDivider.ActualWidth<=0||PreviewHeightDivider.ActualHeight<=0)
                throw new InvalidOperationException("隐藏分栏线破坏了拖动区域。");
        }
        CheckStructuralLines();
        var originalResult=results;var originalEpoch=epoch;var originalRoot=root;var originalSelection=selected;
        var originalGeneration=generation;var originalQueryRequest=queryRequest;var originalFilter=System.Text.Json.JsonSerializer.Serialize(CurrentFilter());
        var initialRadius=BrowserPane.CornerRadius;
        var initialColor=Identity(BrowserPane.Background);
        var alternates=new[]{"native","soft","glass"}.Where(style=>style!=appearance.Style).Select(style=>new FolderLens.Core.AppearancePreferences(style)).ToArray();
        for(int pass=0;pass<3;pass++)
        foreach(var alternateAppearance in alternates)
        {
            ApplyAppearance(alternateAppearance);Shell.UpdateLayout();await Task.Delay(30);CheckStructuralLines();CheckOverlayAppearance();
            if(BrowserPane.CornerRadius==initialRadius)throw new InvalidOperationException("切换外观没有更新布局资源。");
            if(Contrast(ResultSummary.Foreground,Pane())<4.5||Contrast(ActiveFilterSummary.Foreground,Pane())<4.5)throw new InvalidOperationException("外观切换后文字对比度不足。");
            ApplyAppearance(initialAppearance);Shell.UpdateLayout();await Task.Delay(30);CheckStructuralLines();CheckOverlayAppearance();
            if(BrowserPane.CornerRadius!=initialRadius||Identity(BrowserPane.Background)!=initialColor)throw new InvalidOperationException("切回原外观后资源未恢复。");
        }
        report["appearanceState"]=new{sameResults=ReferenceEquals(originalResult,results),sameItems=ReferenceEquals(originalSource,FilesGrid.ItemsSource),sameEpoch=epoch==originalEpoch,sameRoot=root==originalRoot,sameSelection=ReferenceEquals(originalSelection,selected),originalGeneration,generation,originalQueryRequest,queryRequest,sameFilter=originalFilter==System.Text.Json.JsonSerializer.Serialize(CurrentFilter()),queryBusy};
        if(!ReferenceEquals(originalResult,results)||!ReferenceEquals(originalSource,FilesGrid.ItemsSource)||epoch!=originalEpoch||root!=originalRoot||!ReferenceEquals(originalSelection,selected))throw new InvalidOperationException("外观切换改变了浏览状态。");
        if(Shell.RequestedTheme!=ElementTheme.Default)throw new InvalidOperationException("外观切换覆盖了跟随系统设置。");
        await settings!.Save("appearance.json",initialAppearance);
        ApplyAppearance(alternates[0]);await RestoreAppearance();
        if(appearance!=initialAppearance||SoftAppearance.IsChecked!=(initialAppearance.Style=="soft")||NativeAppearance.IsChecked!=(initialAppearance.Style=="native")||GlassAppearance.IsChecked!=(initialAppearance.Style=="glass"))throw new InvalidOperationException("外观选择未保存或菜单状态错误。");
        report["runtimeAppearanceRoundTrips"]=3*alternates.Length;report["appearancePersisted"]=true;report["browsingStatePreserved"]=true;
        viewerTop.Visibility=viewerRight.Visibility=Visibility.Collapsed;Shell.UpdateLayout();
        await Task.Delay(400);// let any overlay hide transition finish before the capture
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
