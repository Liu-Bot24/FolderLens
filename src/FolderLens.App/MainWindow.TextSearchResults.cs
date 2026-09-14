using System.Collections.ObjectModel;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<TextSearchRow> textSearchRows=[];
    private long textSearchResultsRevision;
    private sealed record TextSearchRow(TextMatch Match,string Term,long Revision)
    {
        public string Label=>$"字节 {Match.ByteOffset:N0} · {Match.Preview.Replace('\r',' ').Replace('\n',' ')}";
    }
    private void ClearTextSearchResults()
    {
        textSearchResultsRevision++;textSearchRows.Clear();TextSearchSummary.Text="";
    }
    private void InvalidateTextQuery(object sender,RoutedEventArgs e)
    {
        if(!controlsReady)return;
        textSearchStop.Cancel();textSearchGeneration++;textSearchRunning=false;previousSearch=null;ClearTextSearchResults();
    }
    private async void SearchAllText(object sender,RoutedEventArgs e)
    {
        if(selected?.Item is null||string.IsNullOrEmpty(TextQuery.Text))return;
        textSearchStop.Cancel();textSearchStop.Dispose();textSearchStop=CancellationTokenSource.CreateLinkedTokenSource(selectionStop.Token,textSessionStop.Token);
        var token=textSearchStop.Token;long request=++textSearchGeneration,current=selection;
        ClearTextSearchResults();TextSearchResults.ItemsSource=textSearchRows;
        long revision=textSearchResultsRevision;string term=TextQuery.Text;bool matchCase=TextMatchCase.IsChecked==true;
        bool IsCurrent()=>!closing&&current==selection&&request==textSearchGeneration;
        TextSearchSummary.Text="正在搜索整个文档…";textSearchRunning=true;
        try
        {
            await foreach(var batch in CurrentTextClient().FindAll(term,matchCase,cancellation:token))
            {
                if(!IsCurrent())return;
                foreach(var match in batch.Matches)textSearchRows.Add(new(match,term,revision));
                TextSearchSummary.Text=batch.LimitReached?$"已显示 {textSearchRows.Count:N0} 处，达到展示上限，尚未遍历完整文档。":
                    batch.IsFinal&&batch.Complete?$"全文搜索完成 · {textSearchRows.Count:N0} 处匹配。":
                    $"已找到 {textSearchRows.Count:N0} 处 · 已扫描 {FileRow.FormatBytes(batch.ScannedBytes)}";
            }
        }
        catch(OperationCanceledException){if(IsCurrent())TextSearchSummary.Text=$"已停止，保留 {textSearchRows.Count:N0} 处匹配，未遍历完整文档。";}
        catch(Exception ex){if(IsCurrent())TextSearchSummary.Text="查找失败："+UserMessages.Error(ex);}
        finally{if(request==textSearchGeneration)textSearchRunning=false;}
    }
    private async void OpenTextSearchResult(object sender,ItemClickEventArgs e)
    {
        if(e.ClickedItem is not TextSearchRow row||row.Revision!=textSearchResultsRevision)return;
        long current=selection,sessionVersion=textSessionGeneration;
        try
        {
            await LoadText(row.Match.ByteOffset,current,textSessionStop.Token);
            if(current!=selection||sessionVersion!=textSessionGeneration||row.Revision!=textSearchResultsRevision)return;
            MarkdownHost.Visibility=Visibility.Collapsed;TextScroll.Visibility=Visibility.Visible;
            TextToolsFlyout.Hide();HighlightTextMatch(row.Match,row.Term);
            QualityLabel.Text=$"搜索结果 · 字节 {row.Match.ByteOffset:N0}";
        }
        catch(OperationCanceledException){}catch(Exception ex){if(current==selection)ShowPreviewError(ex);}
    }
    private void HighlightTextMatch(TextMatch match,string term)
    {
        if(displayedText is not {} page)return;
        int index=0;while(index<page.Text.Length&&page.ByteOffsetAt(index)<match.ByteOffset)index++;
        TextContent.Select(index,Math.Min(term.Length,page.Text.Length-index));FocusIfForeground(TextContent,FocusState.Programmatic);
    }
}
