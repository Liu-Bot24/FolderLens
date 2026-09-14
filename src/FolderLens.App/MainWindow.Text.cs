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
    private CancellationTokenSource textSessionStop=new(),textWindowStop=new(),textSearchStop=new();
    private long textSessionGeneration,textWindowGeneration,textSearchGeneration,nextSearchOffset;
    private string? previousSearch;
    private bool previousMatchCase;
    private bool textSearchRunning;
    private bool restoringTextEncoding;
    private TextWindow? displayedText;
    private void OpenReaderSearch(object sender,RoutedEventArgs e){viewerFindPending=true;TextToolsFlyout.ShowAt(TextTools);}
    private async void DecreaseReaderFont(object sender,RoutedEventArgs e)=>await ResizeReaderFont(-2);
    private async void IncreaseReaderFont(object sender,RoutedEventArgs e)=>await ResizeReaderFont(2);
    private async Task ResizeReaderFont(double change)
    {
        TextContent.FontSize=Math.Clamp(TextContent.FontSize+change,12,32);
        try{await ApplyMarkdownTextSize();}
        catch(Exception ex){if(!closing)QualityLabel.Text="无法调整排版字号："+ex.Message;}
    }
    private async Task ApplyMarkdownTextSize()
    {
        // Only this host-owned numeric style is executed; document scripts remain disabled.
        if(markdown?.CoreWebView2 is {} core)
            await core.ExecuteScriptAsync("document.body.style.zoom="+(TextContent.FontSize/16).ToString(System.Globalization.CultureInfo.InvariantCulture)+";");
    }
    private void UpdateReaderControls()
    {
        bool rendered=MarkdownHost.Visibility==Visibility.Visible;
        ReaderRenderMode.Visibility=selected?.Kind=="markdown"?Visibility.Visible:Visibility.Collapsed;
        ReaderRenderMode.Content=rendered?"查看原文":"阅读排版";
        ReaderPreviousPage.Visibility=ReaderNextPage.Visibility=rendered?Visibility.Collapsed:Visibility.Visible;
        ReaderPreviousPage.IsEnabled=displayedText is not null&&textStart>0;
        ReaderNextPage.IsEnabled=displayedText is {} page&&page.Next<page.Length;
    }
    private Task ResetTextSession()
    {
        textSessionStop.Cancel();textSessionStop.Dispose();textSessionStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token,lifetime.Token);textSessionGeneration++;
        textWindowStop.Cancel();textSearchStop.Cancel();textSearchGeneration++;textSearchRunning=false;previousSearch=null;displayedText=null;remoteText=null;TextLineStatus.Text="正在读取文本…";
        textCopyStop?.Cancel();textCopyGeneration++;wholeTextSelected=false;TextSelectionStatus.Text="";ClearTextSearchResults();
        return Task.CompletedTask;
    }
    private RemoteTextClient CurrentTextClient()
    {
        if(selected?.Item is null||contentWorker is null)throw new InvalidOperationException("文档尚未就绪。");
        return remoteText??=new(contentWorker,SourcePath(selected),Context(selected,selection),textEncoding,Stamp(selected));
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
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection&&version==textSessionGeneration&&!closing)TextLineStatus.Text="无法读取文本行号："+ex.Message;}
    }
    private async Task DisposeTextSession()
    {
        textSessionStop.Cancel();textWindowStop.Cancel();textSearchStop.Cancel();textCopyStop?.Cancel();
        if(textIndexBuild is not null)await textIndexBuild;
        await textCopyTask;textCopyStop?.Dispose();
        remoteText=null;textSessionStop.Dispose();textWindowStop.Dispose();textSearchStop.Dispose();
    }
    private async void TextJumpLine(object sender,RoutedEventArgs e)
    {
        long current=selection,version=textSessionGeneration;
        try
        {
            if(!double.IsFinite(TextLineInput.Value)||TextLineInput.Value<1)throw new ArgumentException("请输入从 1 开始的行号。");
            var position=await CurrentTextClient().FindLine(checked((long)TextLineInput.Value),cancellation:textSessionStop.Token);
            if(current!=selection||version!=textSessionGeneration)return;if(position is null){QualityLabel.Text="行号超过文档末尾。";return;}
            await LoadText(position.ByteOffset,current,textSessionStop.Token);if(current==selection)QualityLabel.Text+=$" · 第 {position.LineNumber:N0} 行";
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection)ShowPreviewError(ex);}
    }
    private void WrapText(object sender,RoutedEventArgs e){bool wrap=TextWrapToggle.IsChecked==true;TextContent.TextWrapping=wrap?TextWrapping.Wrap:TextWrapping.NoWrap;TextScroll.HorizontalScrollBarVisibility=wrap?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;}
    private void CancelTextSearch(object sender,RoutedEventArgs e){if(!textSearchRunning)return;textSearchStop.Cancel();textSearchGeneration++;textSearchRunning=false;QualityLabel.Text="查找已停止。";TextSearchSummary.Text=$"已停止，保留 {textSearchRows.Count:N0} 处匹配，未遍历完整文档。";}
    private void TextQueryKeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;TextSearch(sender,new());}}
    private async Task FindText()
    {
        if(selected?.Item is null||string.IsNullOrEmpty(TextQuery.Text))return;
        textSearchStop.Cancel();textSearchStop.Dispose();textSearchStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token,textSessionStop.Token);var token=textSearchStop.Token;
        long generation=++textSearchGeneration,current=selection;string term=TextQuery.Text;bool matchCase=TextMatchCase.IsChecked==true;textSearchRunning=true;
        long start=previousSearch==term&&previousMatchCase==matchCase?nextSearchOffset:textStart;
        var clock=System.Diagnostics.Stopwatch.StartNew();QualityLabel.Text="正在查找…";
        var progress=new Progress<TextSearchBatch>(batch=>{if(current!=selection||generation!=textSearchGeneration||clock.ElapsedMilliseconds<120)return;QualityLabel.Text=$"正在查找 · 已扫描 {FileRow.FormatBytes(batch.ScannedBytes)}";clock.Restart();});
        try
        {
            var result=await CurrentTextClient().FindNext(term,start,matchCase,true,progress,token);
            if(current!=selection||generation!=textSearchGeneration)return;
            if(result.Match is not {} match){QualityLabel.Text=result.SearchExhausted?"整个文档中未找到匹配。":"查找已停止，未遍历完整文档。";return;}
            await LoadText(match.ByteOffset,current,token);if(current!=selection||generation!=textSearchGeneration)return;
            previousSearch=term;previousMatchCase=matchCase;nextSearchOffset=checked(match.ByteOffset+Math.Max(1,match.ByteLength));
            HighlightTextMatch(match,term);
            MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;QualityLabel.Text=$"找到匹配 · 字节 {match.ByteOffset:N0}{(result.Wrapped?" · 已从文档开头继续":"")}";
        }
        catch(OperationCanceledException){if(current==selection&&generation==textSearchGeneration)QualityLabel.Text="查找已停止。";}
        finally{if(generation==textSearchGeneration){textSearchRunning=false;textSearchGeneration++;}}
    }
}
