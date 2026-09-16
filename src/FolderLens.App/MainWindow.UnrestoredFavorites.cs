using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private ContentDialog? unrestoredFavoritesDialog;
    private Func<ContentDialog,Task<ContentDialogResult>>? verifyUnrestoredDialogPresentation;
    private async Task ManageUnrestoredFavorites(string collection)
    {
        if(catalog is null)return;
        var choices=new ListView{Height=260,MinWidth=460,DisplayMemberPath="Path",SelectionMode=ListViewSelectionMode.Single};
        var info=new TextBlock{Text="这些收藏尚未恢复，可能暂时无法访问或缺少身份信息。移除仅删除收藏记录。重新收藏会将记录绑定到该路径当前的文件。",TextWrapping=TextWrapping.Wrap,MaxWidth=620};
        var remove=new Button{Content="移除此记录",IsEnabled=false};var rebind=new Button{Content="重新收藏当前文件",IsEnabled=false};
        var next=new Button{Content="下一页"};var first=new Button{Content="返回第一页"};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};foreach(var button in new[]{remove,rebind,first,next})buttons.Children.Add(button);
        var panel=new StackPanel{Spacing=12};panel.Children.Add(info);panel.Children.Add(choices);panel.Children.Add(buttons);
        long after=0;bool busy=false;IReadOnlyList<UnrestoredPlaylistLink> page=[];
        async Task Reload()
        {
            page=await catalog.ReadUnrestoredPlaylistLinks(collection,after,lifetime.Token);choices.ItemsSource=page;
            next.IsEnabled=page.Count==64;first.IsEnabled=after>0;
            remove.IsEnabled=rebind.IsEnabled=false;rebind.Content="重新收藏当前文件";
            if(page.Count==0)info.Text="此页没有未恢复的收藏记录。";
        }
        choices.SelectionChanged+=(_,_)=>{remove.IsEnabled=rebind.IsEnabled=!busy&&choices.SelectedItem is UnrestoredPlaylistLink;rebind.Content="重新收藏当前文件";};
        async Task Change(bool replace)
        {
            if(busy||choices.SelectedItem is not UnrestoredPlaylistLink link)return;
            if(replace&&(string)rebind.Content!="确认收藏当前文件")
            {rebind.Content="确认收藏当前文件";info.Text="原文件身份无法保证连续。再次点击将明确收藏该路径现在的文件，替换这条旧记录。";return;}
            busy=true;remove.IsEnabled=rebind.IsEnabled=next.IsEnabled=first.IsEnabled=false;
            try
            {
                if(replace)await catalog.RebindUnrestoredPlaylistLink(link,lifetime.Token);else await catalog.RemoveUnrestoredPlaylistLink(link,lifetime.Token);
                await Reload();await RefreshCollectionsTree();info.Text=replace?"已收藏当前位置的文件。":"已移除收藏记录，原文件保留。";
            }
            catch(Exception error){info.Text=UserMessages.Error(error);}
            finally{busy=false;remove.IsEnabled=rebind.IsEnabled=choices.SelectedItem is UnrestoredPlaylistLink;next.IsEnabled=page.Count==64;first.IsEnabled=after>0;}
        }
        remove.Click+=async(_,_)=>await Change(false);rebind.Click+=async(_,_)=>await Change(true);
        next.Click+=async(_,_)=>{try{if(!busy&&page.Count>0){after=page[^1].Id;await Reload();}}catch(Exception error){info.Text=UserMessages.Error(error);}};
        first.Click+=async(_,_)=>{try{if(!busy){after=0;await Reload();}}catch(Exception error){info.Text=UserMessages.Error(error);}};
        await Reload();var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Title="未恢复的收藏",Content=panel,CloseButtonText="关闭"};
        dialog.Closing+=(_,args)=>{if(busy&&!closing&&!lifetime.IsCancellationRequested)args.Cancel=true;};
        unrestoredFavoritesDialog=dialog;
        try{await (verifyUnrestoredDialogPresentation??ShowCollectionDialog)(dialog);}finally{unrestoredFavoritesDialog=null;}
        if(activeCollectionId==collection)await RefreshQuery(preserveViewport:true);
    }
}
