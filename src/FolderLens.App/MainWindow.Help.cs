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
        Section("浏览文件夹","选择顶层文件夹，即可浏览所有子目录中的文件。路径栏旁的“查看层级”可限制向下浏览的层数：当前文件夹算第 1 层，默认所有层级。搜索框只搜索文件名。顶部文件类型、搜索和筛选共同限定列表内容，排序决定显示顺序；按文件夹分组时，先排文件夹，再排组内文件。\n\nCtrl+O  打开文件夹\nCtrl+L  输入文件夹路径\nAlt+← / Alt+→  返回 / 前进\nAlt+↑  上一级文件夹\nF5  刷新目录\nCtrl+F  搜索文件名（阅读文本时搜索正文）");
        Section("查看图片","左侧预览区可选择“窗口预览”或“全屏预览”。双击图片或在文件列表按 Enter 进入全屏；F11 切换全屏与窗口查看，Esc 返回列表。\n\n长按左键临时放大，松开恢复；默认 250%，可在“工具 → 图片查看设置”更改倍率和整图 / 局部模式。小预览始终使用局部放大镜。\n\n滚轮切换图片；Ctrl+滚轮缩放。放大后拖动平移，也可用方向键移动画面；适应屏幕时 ← / → 切换文件。\nPageUp / PageDown  上一个 / 下一个文件\nHome / End  第一项 / 最后一项\nB 或 *  适应屏幕\nCtrl+0  原图 100%\n+ / −  放大 / 缩小\nR / Shift+R  顺时针 / 逆时针旋转画面\n空格  下一张\nCtrl+空格  开始 / 停止幻灯片");
        Section("视频与文件操作","单击视频查看封面，双击使用本地播放器打开。可在“工具 → 播放器设置”选择播放器；播放结果列表需要该播放器支持 M3U8。\n\nCtrl+Shift+C  复制所选文件的完整路径\nShift+F10  打开文件操作菜单\n\n图片旋转只影响显示，应用不会修改原文件。");
        Section("收藏与标签筛选","收藏只记录文件位置，不会复制或移动原文件。图片、视频和其他文件都能放入收藏夹；可在筛选中包含或排除指定收藏夹的内容。文件改名、移动或删除后，原收藏不跟随。\n\n缩略图右上角可快速收藏或取消收藏；右键菜单可选择收藏夹。");
        Section("浏览设置与目录容量","保存浏览设置会记住文件夹、筛选和浏览位置，与收藏文件不同。目录容量统计包含子文件夹；尚未扫描完时显示部分结果，不能据此判断文件夹为空。筛选依赖的尺寸或时长尚未读取时，会单独说明。");
        var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="FolderLens 使用帮助",CloseButtonText="关闭",Content=new ScrollViewer{Content=content,MaxHeight=Math.Max(180,Shell.ActualHeight-180),VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        dialog.Resources["ContentDialogMaxWidth"]=560d;
        AutomationProperties.SetAutomationId(dialog,"LocalHelp");
        localHelpDialog=dialog;
        try{await dialog.ShowAsync();}finally{localHelpDialog=null;}
    }
}
