using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private Action<string> writePreviewClipboard=text=>{var data=new DataPackage();data.SetText(text);Clipboard.SetContent(data);};
    private long previewCopyRevision;
    private async void CopyPreviewLabel(object sender,RoutedEventArgs args)
    {
        if(selected is not {} row||closing)return;
        bool path=(sender as Button)?.Tag as string=="path";
        try
        {
            writePreviewClipboard(path?SourcePath(row):row.Name);
            string notice=path?"已复制完整路径":"已复制文件名";
            PreviewCopyFeedback.Text=notice;PreviewCopyFeedback.Visibility=Visibility.Visible;Status.Text=notice;
            long copied=++previewCopyRevision;
            await Task.Delay(TimeSpan.FromSeconds(2),lifetime.Token);
            if(!closing&&copied==previewCopyRevision)PreviewCopyFeedback.Visibility=Visibility.Collapsed;
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(!closing)ShowError(error);}
    }
    private void VerifyPreviewCopy(Dictionary<string,object> report)
    {
        var original=writePreviewClipboard;var values=new List<string>();
        try
        {
            writePreviewClipboard=values.Add;
            CopyPreviewLabel(CopyPreviewNameButton,new RoutedEventArgs());CopyPreviewLabel(CopyPreviewPathButton,new RoutedEventArgs());
            if(selected is null||values.Count!=2||values[0]!=selected.Name||values[1]!=SourcePath(selected)||!Path.IsPathFullyQualified(values[1]))throw new InvalidOperationException("预览点击复制未使用文件名和完整源路径。");
            if(PreviewCopyFeedback.Visibility!=Visibility.Visible||PreviewCopyFeedback.Text!="已复制完整路径")throw new InvalidOperationException("预览复制缺少可见反馈。");
            report["previewCopyValuesAndFeedback"]=true;report["systemClipboardWrite"]="NOT_RUN";
            writePreviewClipboard=_=>throw new System.Runtime.InteropServices.COMException("Clipboard busy");
            CopyPath(this,new RoutedEventArgs());
            if(!Status.Text.StartsWith("操作未完成"))throw new InvalidOperationException("菜单复制失败未显示错误。");
            report["menuClipboardFailureHandled"]=true;
        }
        finally{writePreviewClipboard=original;}
    }
}
