using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private ContentDialog? localHelpDialog;
    private async void OpenLocalHelp(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.Help);
    private async void RotateCounterclockwise(object sender,RoutedEventArgs e)=>await RunViewerAction(ViewerAction.RotateCounterclockwise);
    private async Task ShowLocalHelp()
    {
        if(localHelpDialog is not null||closing)return;
        var content=new StackPanel{Spacing=18,Width=Math.Clamp(Shell.ActualWidth-80,240,480)};
        void Section(string title,string body)
        {
            var section=new StackPanel{Spacing=6};
            section.Children.Add(new TextBlock{Text=title,FontSize=15,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            section.Children.Add(new TextBlock{Text=body,FontSize=13,TextWrapping=TextWrapping.Wrap});
            content.Children.Add(section);
        }
        Section("浏览文件夹","选择顶层文件夹，即可浏览所有子目录中的文件。类别、筛选和排序共同决定当前结果；按文件夹分组时，先排文件夹，再排组内文件。\n\nCtrl+O  打开文件夹\nCtrl+L  输入文件夹路径\nAlt+← / Alt+→  返回 / 前进\nAlt+↑  上一级文件夹\nF5  刷新目录\nCtrl+F  搜索文件名（阅读文本时搜索正文）");
        Section("查看图片","双击图片或在文件列表按 Enter 进入全屏；F11 切换全屏与窗口查看，Esc 返回列表。\n\n长按左键临时放大，松开恢复；默认 250%，可在“工具 → 图片查看设置”更改倍率和整图 / 局部模式。小预览始终使用局部放大镜。\n\n滚轮切换图片；Ctrl+滚轮缩放。放大后拖动平移，也可用方向键移动画面；适屏时 ← / → 切换文件。\nPageUp / PageDown  上一个 / 下一个文件\nHome / End  第一项 / 最后一项\nB 或 *  适合窗口\nCtrl+0  原图 100%\n+ / −  放大 / 缩小\nR / Shift+R  顺时针 / 逆时针旋转画面\nCtrl+Space  开始 / 停止幻灯片");
        Section("视频与文件操作","单击视频查看封面，双击使用本地播放器打开。可在“工具 → 播放器设置”选择播放器；播放结果列表需要该播放器支持 M3U8。\n\nCtrl+Shift+C  复制所选文件的完整路径\nShift+F10  打开文件操作菜单\n\n图片旋转只影响显示，应用不会修改原文件。");
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="FolderLens 使用帮助",CloseButtonText="关闭",Content=new ScrollViewer{Content=content,MaxHeight=Math.Max(180,Shell.ActualHeight-180),VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        dialog.Resources["ContentDialogMaxWidth"]=560d;
        AutomationProperties.SetAutomationId(dialog,"LocalHelp");
        localHelpDialog=dialog;
        try{await dialog.ShowAsync();}finally{localHelpDialog=null;}
    }
}
