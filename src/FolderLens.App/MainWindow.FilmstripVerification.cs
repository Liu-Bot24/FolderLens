using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFilmstrip(Dictionary<string,object> report)
    {
        viewerStrip!.SelectionChanged-=SelectFile;
        viewerStrip.ItemsSource=Enumerable.Range(0,300).Select(i=>new {Name=$"图片 {i}"}).ToArray();
        viewerTop!.Visibility=Visibility.Visible;Shell.UpdateLayout();viewerStrip.ApplyTemplate();viewerStrip.UpdateLayout();
        var scroll=FindScrollViewer(viewerStrip)??throw new InvalidOperationException("缩略图条没有滚动控件");
        report["horizontalMode"]=scroll.HorizontalScrollMode.ToString();report["verticalMode"]=scroll.VerticalScrollMode.ToString();
        if(scroll.HorizontalScrollMode==ScrollMode.Disabled||scroll.VerticalScrollMode!=ScrollMode.Disabled)
            throw new InvalidOperationException("缩略图条仍禁止横向滚动或允许纵向滚动");
        if(scroll.ScrollableWidth<=0)throw new InvalidOperationException("横向滚动范围为空");
        var provider=(IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll);
        provider.SetScrollPercent(25,-1);await WaitUntil(()=>scroll.HorizontalOffset>0,TimeSpan.FromSeconds(3));
        double offset=scroll.HorizontalOffset;report["scrollOffset"]=offset;
        // Layout may replace the template's peer after the first virtualized scroll.
        scroll=FindScrollViewer(viewerStrip)??throw new InvalidOperationException("缩略图条没有滚动控件");
        provider=(IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll);
        provider.SetScrollPercent(0,-1);await WaitUntil(()=>scroll.HorizontalOffset<1,TimeSpan.FromSeconds(3));
        report["status"]="PASS";
    }
}
