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
        await File.WriteAllTextAsync(Path.Combine(source,"reader.md"),"# 阅读标题\n\n正文中文 **加粗**。\n\n![本地图片](A/image-00.png)\n\n![禁止越界](../outside.png)\n");
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
                await WaitUntil(()=>markdownImages.Count==1,TimeSpan.FromSeconds(10));
                string imageReady=await markdown.CoreWebView2.ExecuteScriptAsync("Array.from(document.images).some(image=>image.naturalWidth>0)");
                if(imageReady!="true")failures.Add("允许的本地图片没有在 Markdown 阅读区显示");
                report["isolatedMarkdownImageDisplayed"]=imageReady=="true";
                ((IInvokeProvider)new ButtonAutomationPeer(ReaderRenderMode).GetPattern(PatternInterface.Invoke)).Invoke();
                if(TextScroll.Visibility!=Visibility.Visible||ReaderRenderMode.Content as string!="阅读排版")failures.Add("查看原文没有正确切换状态");
            }
            else if(ReaderRenderMode.Visibility!=Visibility.Collapsed)failures.Add("TXT 出现了不适用的排版按钮");
            await ResizeReaderFont(-2);

            var list=FilesGrid.ItemsSource;string beforeRoot=root;var current=selected;
            await SetImmersive(true);
            Shell.UpdateLayout();
            UpdateReaderControls();
            if(TextScroll.ScrollableHeight<=0.5&&(ReaderNextPage.IsEnabled||ReaderPreviousPage.IsEnabled))failures.Add("完整显示的短文档仍允许翻到空白页");
            if(WindowPreviewButton.Content as string!="返回列表"||PreviewReturn.Visibility!=Visibility.Collapsed||PreviewActions.Visibility!=Visibility.Collapsed)failures.Add("预览入口或多余工具行不符合当前阅读布局");
            if(!BackButton.IsEnabled)failures.Add("窗口预览时返回按钮未启用");
            await NavigateHistory(false);
            if(immersive)await ReturnToBrowser();
            if(root!=beforeRoot||!ReferenceEquals(list,FilesGrid.ItemsSource)||!ReferenceEquals(current,selected))failures.Add("返回阅读列表改变了目录、结果或选择");
        }
        await ReturnToBrowser();
        string longText=string.Concat(Enumerable.Range(0,3000).Select(i=>$"第{i:D4}行 中文与 emoji 🐈 文本翻页边界。\r\n"));
        await File.WriteAllTextAsync(Path.Combine(source,"long-reader.txt"),longText);
        await RefreshCurrentRoot();await RefreshQuery();
        long longOrdinal=(await catalog!.FindOrdinal(resultHandle!.Id,"long-reader.txt"))!.Value;
        await SelectBrowserOrdinal(results!,checked((int)longOrdinal),lifetime.Token);await SetImmersive(true);Shell.UpdateLayout();
        long initialStart=textStart,initialNext=textNext;string initialText=TextContent.Text;
        await PageReader(1);Shell.UpdateLayout();
        if(textStart!=initialStart||TextScroll.VerticalOffset<=0)failures.Add("向下翻页没有先滚动当前读取块");
        TextScroll.ChangeView(null,TextScroll.ScrollableHeight,null,true);await Task.Delay(50);
        await PageReader(1);Shell.UpdateLayout();
        if(textStart!=initialNext||TextScroll.VerticalOffset>1)failures.Add("块末尾没有连续进入下一读取块顶部");
        await PageReader(-1);Shell.UpdateLayout();
        if(textStart!=initialStart||TextContent.Text!=initialText||TextScroll.VerticalOffset<TextScroll.ScrollableHeight-1)failures.Add("返回上一块丢字、重复或未定位块底部");
        await LoadText(0,selection,selectionStop.Token);Shell.UpdateLayout();await PageReader(-1);
        if(textStart!=0||TextScroll.VerticalOffset>1)failures.Add("文档开头向上翻页越界");
        report["longTextViewportAndChunkNavigation"]=failures.Count==0;
        await ReturnToBrowser();
        report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException(string.Join("；",failures));
        report["status"]="PASS";
    }
}
