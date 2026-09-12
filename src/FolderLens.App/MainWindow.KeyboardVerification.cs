using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyKeyboardCompletion(string source,Dictionary<string,object> report)
    {
        ViewerBinding Binding(VirtualKey key,VirtualKeyModifiers modifiers)=>ViewerBindings.Single(b=>b.Key==key&&b.Modifiers==modifiers);
        var help=Binding(VirtualKey.F1,0);
        var reverse=Binding(VirtualKey.R,VirtualKeyModifiers.Shift);
        var copy=Binding(VirtualKey.C,VirtualKeyModifiers.Control|VirtualKeyModifiers.Shift);
        if(help.Action!=ViewerAction.Help||reverse.Action!=ViewerAction.RotateCounterclockwise||copy.Action!=ViewerAction.CopyFilePath)throw new InvalidOperationException("快捷键没有接到对应操作。");
        if(!CanRunViewerBinding(help,Search)||CanRunViewerBinding(copy,Search)||CanRunViewerBinding(reverse,Search))throw new InvalidOperationException("快捷键输入框边界错误。");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);
        await WaitUntil(()=>fitBitmap is not null&&!previewLoading,TimeSpan.FromSeconds(5));
        if(!CanRunViewerBinding(copy,FilesGrid)||!CanRunViewerBinding(reverse,ImageInput)||CanRunViewerBinding(reverse,new NumberBox()))throw new InvalidOperationException("图片/输入框快捷键边界错误。");
        int original=rotation;
        await RunViewerAction(reverse.Action);if(rotation!=(original+3)%4)throw new InvalidOperationException("逆时针旋转错误。");
        await RunViewerAction(ViewerAction.Rotate);if(rotation!=original)throw new InvalidOperationException("顺逆旋转没有相互还原。");
        for(int i=0;i<4;i++)await RunViewerAction(reverse.Action);
        if(rotation!=original)throw new InvalidOperationException("四次逆时针旋转未还原。");
        var image=selected;selected=new FileRow(0);selected.Fill(new(0,"video",1,"clip.mp4","",0,null,"video"));
        if(!CanRunViewerBinding(copy,FilesGrid)||CanRunViewerBinding(reverse,ImageInput))throw new InvalidOperationException("视频允许复制路径，但不允许图片旋转。");
        selected=new FileRow(0);selected.Fill(new(0,"audio",1,"cover.mp3","",0,null,"audio"));
        // Keep a decoded cover bitmap, matching the audio preview state. Menu
        // actions enter the executor directly, bypassing keyboard focus guards.
        await RunViewerAction(ViewerAction.RotateCounterclockwise);
        if(rotation!=original)throw new InvalidOperationException("逆向菜单旋转了音频封面。");
        await RunViewerAction(ViewerAction.Rotate);
        if(rotation!=original)throw new InvalidOperationException("正向菜单旋转了音频封面。");
        report["audioMenuDoesNotRotate"]=true;
        selected=image;
        Task opening=RunViewerAction(help.Action);
        try
        {
            await WaitUntil(()=>localHelpDialog is {IsLoaded:true},TimeSpan.FromSeconds(3));
            if(CanRunViewerBinding(reverse,localHelpDialog)||CanRunViewerBinding(help,localHelpDialog))throw new InvalidOperationException("帮助弹层未隔离快捷键。");
            var shown=localHelpDialog;await ShowLocalHelp();
            if(!ReferenceEquals(shown,localHelpDialog))throw new InvalidOperationException("重复F1叠加了帮助弹层。");
            report["helpOpenedLocally"]=true;
        }
        finally{localHelpDialog?.Hide();await opening;}
        if(localHelpDialog is not null)throw new InvalidOperationException("帮助关闭后未释放状态。");
        report["reverseRotationRestored"]=true;report["editorAndMediaGuards"]=true;
        report["clipboardVerification"]="Shortcut dispatch and focus guards verified; the user's clipboard was not changed.";
        report["status"]="PASS";
    }
}
