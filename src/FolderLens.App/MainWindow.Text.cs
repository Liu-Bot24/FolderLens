using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private RemoteTextClient? remoteText;
    private Task? textIndexBuild;
    private CancellationTokenSource textSessionStop=new(),textPresentationStop=new(),textWindowStop=new(),textSearchStop=new();
    private long textSessionGeneration,textWindowGeneration,textSearchGeneration,nextSearchOffset;
    private string? previousSearch;
    private bool previousMatchCase;
    private bool textSearchRunning;
    private bool restoringTextEncoding;
    private TextWindow? displayedText;
    private bool readerPaging;
    private readonly record struct TextNavigation(long Generation,CancellationToken Token);
    private CancellationToken BeginOriginalTextPresentation()
    {
        CancelMarkdownPresentation();
        if(textPresentationStop.IsCancellationRequested)
        {textPresentationStop.Dispose();textPresentationStop=CancellationTokenSource.CreateLinkedTokenSource(textSessionStop.Token,lifetime.Token);}
        MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;UpdateReaderControls();
        return textPresentationStop.Token;
    }
    private void CancelOriginalTextPresentation()
    {
        // Reading/indexing the document is a separate lifetime. Retire only
        // operations that could present an original-text window or its status.
        textPresentationStop.Cancel();textWindowStop.Cancel();textSearchStop.Cancel();
        textWindowGeneration++;textSearchGeneration++;textSearchRunning=false;
    }
    private TextNavigation BeginTextNavigation(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var presentation=BeginOriginalTextPresentation();
        // Lookup, result-click and paging requests share one visible text
        // destination. Retire its prior operation before resolving a new one.
        textWindowStop.Cancel();textWindowStop.Dispose();
        textWindowStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,textSessionStop.Token,presentation);
        return new(++textWindowGeneration,textWindowStop.Token);
    }
    private bool OwnsTextNavigation(TextNavigation navigation)
        =>!closing&&navigation.Generation==textWindowGeneration&&!navigation.Token.IsCancellationRequested;
    private async Task PageReader(int direction)
    {
        if(readerPaging||selected?.Kind is not ("text" or "markdown"))return;
        readerPaging=true;long current=selection;var presentation=MarkdownPresentationRequested?markdownPresentationStop!.Token:textPresentationStop.Token;
        try
        {
            if(MarkdownHost.Visibility==Visibility.Visible&&markdown?.CoreWebView2 is {} core)
            {await core.ExecuteScriptAsync($"window.scrollBy(0,window.innerHeight*{(direction<0?"-0.9":"0.9")})");return;}
            if(displayedText is null)return;
            var navigation=BeginTextNavigation(selectionStop.Token);presentation=navigation.Token;
            double offset=TextScroll.VerticalOffset,extent=TextScroll.ScrollableHeight;
            if(direction<0&&offset>0.5||direction>0&&offset<extent-0.5)
            {TextScroll.ChangeView(null,Math.Clamp(offset+direction*Math.Max(32,TextScroll.ViewportHeight*.9),0,extent),null,true);return;}
            if(direction>0&&textNext<displayedText.Length)await ReadTextWindow(textNext,current,navigation);
            else if(direction<0&&textStart>0)
            {await ReadTextWindow(Math.Max(0,textStart-32*1024),current,navigation);if(current==selection&&OwnsTextNavigation(navigation)){TextScroll.UpdateLayout();TextScroll.ChangeView(null,TextScroll.ScrollableHeight,null,true);}}
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection&&!presentation.IsCancellationRequested&&!closing)ShowPreviewError(ex);}
        finally{readerPaging=false;UpdateReaderControls();}
    }
    private async void ReaderWheel(object sender,PointerRoutedEventArgs e)
    {
        if(ViewerModifiers()!=VirtualKeyModifiers.None||TextScroll.Visibility!=Visibility.Visible)return;
        var point=e.GetCurrentPoint(TextScroll);if(point.Properties.IsHorizontalMouseWheel||point.Properties.MouseWheelDelta==0)return;
        e.Handled=true;await PageReader(point.Properties.MouseWheelDelta>0?-1:1);
    }
    private void OpenReaderSearch(object sender,RoutedEventArgs e){viewerFindPending=true;TextToolsFlyout.ShowAt(TextTools);}
    private async void DecreaseReaderFont(object sender,RoutedEventArgs e)=>await ResizeReaderFont(-2);
    private async void IncreaseReaderFont(object sender,RoutedEventArgs e)=>await ResizeReaderFont(2);
    private async Task ResizeReaderFont(double change)
    {
        long current=selection;var view=markdown;
        var presentation=MarkdownPresentationRequested?markdownPresentationStop!.Token:textPresentationStop.Token;
        TextContent.FontSize=Math.Clamp(TextContent.FontSize+change,12,32);
        try{await ApplyMarkdownTextSize();}
        catch(Exception ex){if(!closing&&current==selection&&ReferenceEquals(markdown,view)&&!presentation.IsCancellationRequested)QualityLabel.Text="无法调整排版字号："+UserMessages.Error(ex);}
    }
    private async Task ApplyMarkdownTextSize()
    {
        // Only this host-owned numeric style is executed; document scripts remain disabled.
        if(markdown?.CoreWebView2 is {} core)
        {
            if(verifyMarkdownTextSizeBarrier is not null)await verifyMarkdownTextSizeBarrier();
            await core.ExecuteScriptAsync("document.body.style.zoom="+(TextContent.FontSize/16).ToString(System.Globalization.CultureInfo.InvariantCulture)+";");
        }
    }
    private void UpdateReaderControls()
    {
        bool rendered=MarkdownHost.Visibility==Visibility.Visible;
        ReaderRenderMode.Visibility=selected?.Kind=="markdown"?Visibility.Visible:Visibility.Collapsed;
        ReaderRenderMode.Content=rendered||MarkdownPresentationRequested?"查看原文":"阅读排版";
        ReaderPreviousPage.Visibility=ReaderNextPage.Visibility=Visibility.Visible;
        ReaderPreviousPage.IsEnabled=rendered||displayedText is not null&&(textStart>0||TextScroll.VerticalOffset>0.5);
        ReaderNextPage.IsEnabled=rendered||displayedText is {} page&&(page.Next<page.Length||TextScroll.VerticalOffset<TextScroll.ScrollableHeight-0.5);
    }
    private Task ResetTextSession()
    {
        textSessionStop.Cancel();textSessionStop.Dispose();textSessionStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token,lifetime.Token);textSessionGeneration++;
        textPresentationStop.Cancel();textPresentationStop.Dispose();textPresentationStop=CancellationTokenSource.CreateLinkedTokenSource(textSessionStop.Token,lifetime.Token);
        textWindowStop.Cancel();textSearchStop.Cancel();textSearchGeneration++;textSearchRunning=false;previousSearch=null;displayedText=null;remoteText=null;TextContent.Text="";TextLineStatus.Text="正在读取文本…";
        textCopyStop?.Cancel();textCopyGeneration++;wholeTextSelected=false;TextSelectionStatus.Text="";ClearTextSearchResults();
        return Task.CompletedTask;
    }
    private RemoteTextClient CurrentTextClient()
    {
        if(selected?.Item is null||contentWorker is null)throw new InvalidOperationException("文档尚未就绪。");
        return remoteText??=new(contentWorker,SourcePath(selected),Context(selected,selection),textEncoding,Stamp(selected),approvedCloud.Contains(CloudKey(selected)));
    }
    private Task StartTextIndex(long current,CancellationToken token)
    {
        if(selected?.Item is null)return Task.CompletedTask;
        long version=textSessionGeneration;var client=CurrentTextClient();
        textIndexBuild=BuildTextIndex(client,current,version,textSessionStop.Token);
        return Task.CompletedTask;
    }
    private async Task BuildTextIndex(RemoteTextClient client,long current,long version,CancellationToken token)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while(true)
            {
                var progress=await client.IndexStep(4,token);
                if(current!=selection||version!=textSessionGeneration||closing)return;
                if(progress.Complete||clock.ElapsedMilliseconds>=200)
                {TextLineStatus.Text=progress.Complete?$"共 {progress.TotalLines:N0} 行":$"已读取 {FileRow.FormatBytes(progress.IndexedBytes)} / {FileRow.FormatBytes(progress.Length)}";clock.Restart();}
                if(progress.Complete)return;
                await Task.Delay(10,token);
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection&&version==textSessionGeneration&&!closing)TextLineStatus.Text="无法读取文本行号："+UserMessages.Error(ex);}
    }
    private async Task DisposeTextSession()
    {
        textSessionStop.Cancel();textPresentationStop.Cancel();textWindowStop.Cancel();textSearchStop.Cancel();textCopyStop?.Cancel();
        if(textIndexBuild is not null)await textIndexBuild;
        await textCopyTask;textCopyStop?.Dispose();
        remoteText=null;textSessionStop.Dispose();textPresentationStop.Dispose();textWindowStop.Dispose();textSearchStop.Dispose();
    }
    private async void TextJumpLine(object sender,RoutedEventArgs e)=>await JumpToTextLine();
    private async Task JumpToTextLine()
    {
        long current=selection,version=textSessionGeneration;var navigation=BeginTextNavigation(selectionStop.Token);var token=navigation.Token;
        try
        {
            if(!double.IsFinite(TextLineInput.Value)||TextLineInput.Value<1)throw new ArgumentException("请输入从 1 开始的行号。");
            var client=CurrentTextClient();long line=checked((long)TextLineInput.Value);
            if(verifyTextFindLineBarrier is not null)await verifyTextFindLineBarrier(token);
            var position=await client.FindLine(line,cancellation:token);
            if(current!=selection||version!=textSessionGeneration||!OwnsTextNavigation(navigation))return;if(position is null){QualityLabel.Text="行号超过文档末尾。";return;}
            await ReadTextWindow(position.ByteOffset,current,navigation);if(current==selection&&version==textSessionGeneration&&OwnsTextNavigation(navigation))QualityLabel.Text+=$" · 第 {position.LineNumber:N0} 行";
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection&&version==textSessionGeneration&&OwnsTextNavigation(navigation))ShowPreviewError(ex);}
    }
    private void WrapText(object sender,RoutedEventArgs e){bool wrap=TextWrapToggle.IsChecked==true;TextContent.TextWrapping=wrap?TextWrapping.Wrap:TextWrapping.NoWrap;TextScroll.HorizontalScrollBarVisibility=wrap?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;}
    private void CancelTextSearch(object sender,RoutedEventArgs e){if(!textSearchRunning)return;textSearchStop.Cancel();textSearchGeneration++;textSearchRunning=false;QualityLabel.Text="查找已停止。";TextSearchSummary.Text=$"已停止，保留 {textSearchRows.Count:N0} 处匹配，未遍历完整文档。";}
    private void TextQueryKeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;TextSearch(sender,new());}}
    private async Task FindText()
    {
        if(selected?.Item is null||string.IsNullOrEmpty(TextQuery.Text))return;
        var presentation=BeginOriginalTextPresentation();
        textSearchStop.Cancel();textSearchStop.Dispose();textSearchStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token,textSessionStop.Token,presentation);
        var navigation=BeginTextNavigation(textSearchStop.Token);var token=navigation.Token;
        long generation=++textSearchGeneration,current=selection;string term=TextQuery.Text;bool matchCase=TextMatchCase.IsChecked==true;textSearchRunning=true;
        long start=previousSearch==term&&previousMatchCase==matchCase?nextSearchOffset:textStart;
        var clock=System.Diagnostics.Stopwatch.StartNew();QualityLabel.Text="正在查找…";
        var progress=new Progress<TextSearchBatch>(batch=>{if(current!=selection||generation!=textSearchGeneration||!OwnsTextNavigation(navigation)||clock.ElapsedMilliseconds<120)return;QualityLabel.Text=$"正在查找 · 已扫描 {FileRow.FormatBytes(batch.ScannedBytes)}";clock.Restart();});
        try
        {
            var client=CurrentTextClient();
            if(verifyTextFindNextBarrier is not null)await verifyTextFindNextBarrier(token);
            var result=await client.FindNext(term,start,matchCase,true,progress,token);
            if(current!=selection||generation!=textSearchGeneration||!OwnsTextNavigation(navigation))return;
            if(result.Match is not {} match){QualityLabel.Text=result.SearchExhausted?"整个文档中未找到匹配。":"查找已停止，未遍历完整文档。";return;}
            await ReadTextWindow(match.ByteOffset,current,navigation);if(current!=selection||generation!=textSearchGeneration||!OwnsTextNavigation(navigation))return;
            previousSearch=term;previousMatchCase=matchCase;nextSearchOffset=checked(match.ByteOffset+Math.Max(1,match.ByteLength));
            HighlightTextMatch(match,term);
            MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;QualityLabel.Text=$"找到匹配 · 字节 {match.ByteOffset:N0}{(result.Wrapped?" · 已从文档开头继续":"")}";
        }
        catch(OperationCanceledException){if(current==selection&&generation==textSearchGeneration&&OwnsTextNavigation(navigation))QualityLabel.Text="查找已停止。";}
        catch(Exception ex){if(current==selection&&generation==textSearchGeneration&&OwnsTextNavigation(navigation))ShowPreviewError(ex);}
        finally{if(generation==textSearchGeneration){textSearchRunning=false;textSearchGeneration++;}}
    }
}
