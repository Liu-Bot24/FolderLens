using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyTextReader(string source,Dictionary<string,object> report)
    {
        const string text="文本阅读测试：中文与 English\r\n第二行完整显示。";
        await File.WriteAllTextAsync(Path.Combine(source,"reader.txt"),text);
        await File.WriteAllTextAsync(Path.Combine(source,"reader.md"),"# 阅读标题\n\n正文中文 **加粗**。\n");
        suppressFilters=true;SelectTag(Category,"text");suppressFilters=false;
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var failures=new List<string>();
        foreach(string name in new[]{"reader.txt","reader.md"})
        {
            long ordinal=(await catalog!.FindOrdinal(resultHandle!.Id,name))!.Value;
            await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);
            if(previewReadySelection!=selection)failures.Add(name+"加载失败："+QualityLabel.Text);
            if(name.EndsWith("txt"))
            {
                report["textDisplayed"]=TextContent.Text;
                if(TextContent.Text.ReplaceLineEndings("\n")!=text.ReplaceLineEndings("\n"))failures.Add("TXT正文未完整显示");
            }
            if(name.EndsWith("md")&&MarkdownHost.Visibility!=Visibility.Visible)failures.Add("Markdown排版未显示："+QualityLabel.Text);
            await ResizeReaderFont(2);
            if(TextContent.FontSize!=18)failures.Add("放大文字没有更新字号");
            if(name.EndsWith("md"))
            {
                if(markdown?.CoreWebView2 is null)throw new InvalidOperationException("Markdown 尚未就绪："+QualityLabel.Text+"；"+string.Join("；",failures));
                string zoomValue=await markdown!.CoreWebView2.ExecuteScriptAsync("document.body.style.zoom");
                if(zoomValue!="\"1.125\"")failures.Add("Markdown 字号没有跟随阅读按钮");
                if(markdown.CoreWebView2.Settings.IsScriptEnabled)failures.Add("阅读字号意外允许文档脚本执行");
                ((IInvokeProvider)new ButtonAutomationPeer(ReaderRenderMode).GetPattern(PatternInterface.Invoke)).Invoke();
                if(TextScroll.Visibility!=Visibility.Visible||ReaderRenderMode.Content as string!="阅读排版")failures.Add("查看原文没有正确切换状态");
            }
            else if(ReaderRenderMode.Visibility!=Visibility.Collapsed)failures.Add("TXT 出现了不适用的排版按钮");
            await ResizeReaderFont(-2);
            if(ReaderNextPage.IsEnabled||ReaderPreviousPage.IsEnabled)failures.Add("单页文档仍允许翻到空白页");
            var list=FilesGrid.ItemsSource;string beforeRoot=root;var current=selected;
            await SetImmersive(true);
            Shell.UpdateLayout();
            if(PreviewReturn.ActualWidth<96)failures.Add("返回文件列表文字被图标按钮宽度裁切");
            var exit=PreviewActions.Children.OfType<Button>().FirstOrDefault(b=>b.Content as string=="返回文件列表");
            if(exit is null||exit.Visibility!=Visibility.Visible)failures.Add("阅读页没有返回文件列表入口");
            else
            {
                ((IInvokeProvider)new ButtonAutomationPeer(exit).GetPattern(PatternInterface.Invoke)).Invoke();
                await WaitUntil(()=>!immersive,TimeSpan.FromSeconds(3));
            }
            if(immersive)await ReturnToBrowser();
            if(root!=beforeRoot||!ReferenceEquals(list,FilesGrid.ItemsSource)||!ReferenceEquals(current,selected))failures.Add("返回阅读列表改变了目录、结果或选择");
        }
        report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException(string.Join("；",failures));
        report["status"]="PASS";
    }
}
