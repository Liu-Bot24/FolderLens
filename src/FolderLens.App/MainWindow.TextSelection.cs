using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool wholeTextSelected;
    private CancellationTokenSource? textCopyStop;
    private Task textCopyTask=Task.CompletedTask;
    private long textCopyGeneration;
    private void SelectWholeText(object sender,RoutedEventArgs e)
    {
        if(displayedText is null)return;
        TextContent.SelectAll();wholeTextSelected=true;
        TextSelectionStatus.Text="已选择整个文档。最多可复制约 800 万个字符；内容过多时请分段复制。";
    }
    private void TextSelectionChanged(object sender,RoutedEventArgs e)
    {
        wholeTextSelected=false;
        if(TextSelectionStatus is not null)TextSelectionStatus.Text="";
    }
    private void TextPreviewKeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(ViewerModifiers()!=VirtualKeyModifiers.Control)return;
        if(e.Key==VirtualKey.A){e.Handled=true;SelectWholeText(sender,new());}
        else if(e.Key==VirtualKey.C){e.Handled=true;CopyTextSelection(sender,new());}
    }
    private void CancelTextCopy(object sender,RoutedEventArgs e)
    {
        if(textCopyTask.IsCompleted)return;
        textCopyStop?.Cancel();textCopyGeneration++;TextSelectionStatus.Text="复制已停止，剪贴板未更改。";
    }
    private void CopyTextSelection(object sender,RoutedEventArgs e)=>textCopyTask=CopyTextSelection();
    private async Task CopyTextSelection()
    {
        if(displayedText is null||selected?.Item is null)return;
        textCopyStop?.Cancel();textCopyStop?.Dispose();textCopyStop=CancellationTokenSource.CreateLinkedTokenSource(textSessionStop.Token,lifetime.Token);
        var token=textCopyStop.Token;long current=selection,sessionVersion=textSessionGeneration,request=++textCopyGeneration;
        bool IsCurrent()=>!closing&&current==selection&&sessionVersion==textSessionGeneration&&request==textCopyGeneration;
        try
        {
            string value;
            if(wholeTextSelected)
            {
                TextSelectionStatus.Text="正在复制全文…";
                var progress=new Progress<long>(bytes=>{if(IsCurrent())TextSelectionStatus.Text=$"正在复制 · 已读取 {FileRow.FormatBytes(bytes)}";});
                value=await CurrentTextClient().ReadDocumentForCopy(progress:progress,cancellation:token);
            }
            else value=TextContent.SelectedText;
            token.ThrowIfCancellationRequested();if(!IsCurrent())return;
            if(value.Length==0){TextSelectionStatus.Text="请选择要复制的文字。";return;}
            var data=new DataPackage();data.SetText(value);Clipboard.SetContent(data);
            // Retire queued progress notifications before presenting completion.
            textCopyGeneration++;TextSelectionStatus.Text=$"已复制 {value.Length:N0} 个字符。";
        }
        catch(OperationCanceledException){if(IsCurrent())TextSelectionStatus.Text="复制已停止，剪贴板未更改。";}
        catch(Exception ex){if(IsCurrent())TextSelectionStatus.Text="无法复制："+ex.Message;}
        finally{if(request==textCopyGeneration)textCopyGeneration++;}
    }
}
