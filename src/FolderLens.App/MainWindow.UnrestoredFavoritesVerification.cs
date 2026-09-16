using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyUnrestoredFavorites(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;monitor?.Dispose();monitor=null;
        string collection=(await catalog!.CreateCollection("未恢复记录验证")).Id;
        await catalog.ChangeCollectionItems([collection],(await catalog.ReadFirstPage(CurrentFilter())).Items.Take(2).ToArray(),true);
        await catalog.Write(c=>{using var q=c.CreateCommand();q.CommandText="UPDATE playlist.SavedLinks SET anchor='',directory_identity='',location_key='legacy-'||rowid,file_identity=NULL; DELETE FROM CollectionMembers;";return q.ExecuteNonQuery();});
        // Verification windows deliberately never acquire foreground. Present the
        // actual dialog here without waiting for a user's activation gesture.
        verifyUnrestoredDialogPresentation=async dialog=>await dialog.ShowAsync();
        Task shown=ManageUnrestoredFavorites(collection);
        try
        {
            await WaitUntil(()=>unrestoredFavoritesDialog?.IsLoaded==true,TimeSpan.FromSeconds(10));
            var dialog=unrestoredFavoritesDialog!;var panel=(StackPanel)dialog.Content;var list=(ListView)panel.Children[1];var buttons=(StackPanel)panel.Children[2];
            if(list.Items.Count!=2)throw new InvalidOperationException("未恢复记录没有显示。");
            void Invoke(Button button)=>((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            list.SelectedIndex=0;Invoke((Button)buttons.Children[0]);await WaitUntil(()=>list.Items.Count==1,TimeSpan.FromSeconds(5));
            list.SelectedIndex=0;var rebind=(Button)buttons.Children[1];Invoke(rebind);
            if((string)rebind.Content!="确认收藏当前文件"||(await catalog.ReadFirstPage(new(){RootId="collection:"+collection,CollectionId=collection})).Items.Count!=0)
                throw new InvalidOperationException("未确认便自动绑定了当前文件。");
            Invoke(rebind);await WaitUntil(()=>list.Items.Count==0,TimeSpan.FromSeconds(10));
            if((await catalog.ReadFirstPage(new(){RootId="collection:"+collection,CollectionId=collection})).Items.Count!=1||(await catalog.ReadCollections()).Single(c=>c.Id==collection).Count!=1)
                throw new InvalidOperationException("重新收藏后计数与实际成员不一致。");
            report["unrestoredRecordsVisibleAndRemovable"]=true;report["explicitRebindRequiresConfirmation"]=true;report["status"]="PASS";
        }
        finally{unrestoredFavoritesDialog?.Hide();verifyUnrestoredDialogPresentation=null;await shown.WaitAsync(TimeSpan.FromSeconds(5));}
    }
}
