using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyCollections(string source,Dictionary<string,object> report)
    {
        string a=Path.Combine(source,"A"),b=Path.Combine(source,"B");
        string image=Directory.GetFiles(a,"*.png")[0];string copy=Path.Combine(b,Path.GetFileName(image));File.Copy(image,copy);
        await File.WriteAllTextAsync(Path.Combine(b,"结案.txt"),"跨目录收藏文本验证");await File.WriteAllTextAsync(Path.Combine(b,"结案.pptx"),"classification fixture");
        report["phase"]="collectA";await OpenRoot(a);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var collection=await catalog!.CreateCollection("家具");var excluded=await catalog.CreateCollection("NSFW");
        var handle=resultHandle!;await catalog.RetainSnapshot(handle.Id);
        try{if(await catalog.ChangeCollectionSelection([collection.Id],handle.Id,[new(0,2)],true)!=2)throw new InvalidOperationException("批量收藏未写入完整选择。");}
        finally{await catalog.ReleaseSnapshot(handle.Id);}
        report["phase"]="collectB";ApplySavedFilter(new(){RootId=rootId,Kinds=[]});await OpenRoot(b);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        var items=await catalog.ReadPage(resultHandle!.Id,0);await catalog.ChangeCollectionMembers([collection.Id],items.Select(i=>i.EntryId).ToArray(),true);
        await catalog.ChangeCollectionMembers([excluded.Id],items.Where(i=>i.Kind=="image").Select(i=>i.EntryId).ToArray(),true);
        await RefreshCollectionsTree();report["phase"]="openCollection";await OpenCollection(collection.Id);
        if(activeCollectionId!=collection.Id||results?.Count!=5||scanTask is not null)throw new InvalidOperationException("跨目录收藏列表错误或触发了源目录扫描。");
        var rows=await catalog.ReadPage(resultHandle!.Id,0);if(rows.Select(i=>i.SourceRootPath).Distinct().Count()!=2)throw new InvalidOperationException("收藏快照丢失源目录。");
        foreach(var item in rows)if(await catalog.FindOrdinal(resultHandle.Id,Path.Combine(item.SourceRootPath!,item.RelativePath))!=item.Ordinal)throw new InvalidOperationException("不同目录的同名文件定位混淆。");
        report["phase"]="previewAcrossRoots";
        await catalog.Write(c=>{using var command=c.CreateCommand();command.CommandText="DELETE FROM FieldStates WHERE entry_id IN(SELECT f.entry_id FROM Files f JOIN CollectionMembers m ON m.location_key=f.location_key WHERE m.collection_id=$collection);UPDATE Files SET display_width=NULL,display_height=NULL,long_edge=NULL,short_edge=NULL,pixel_count=NULL WHERE kind='image'";command.Parameters.AddWithValue("$collection",collection.Id);return command.ExecuteNonQuery();});
        await RestoreSavedView(new SavedView("collection:"+collection.Id,new(){RootId="collection:"+collection.Id,CollectionId=collection.Id,Ranges=new(){["width"]=new(64,null)}},null,0,false));
        if(metadataTask is null)throw new InvalidOperationException("恢复收藏筛选未自动启动元数据补充。");
        await metadataTask;
        if(results?.Count!=3)throw new InvalidOperationException("收藏尺寸筛选未自动发布补充结果。");
        await OpenCollection(collection.Id);
        foreach(var item in rows.Where(i=>i.Kind=="image"))if((await catalog.ReadFileProperties(item.SourceRootId!,item.EntryId,item.Version))?.Width!=64)throw new InvalidOperationException("收藏来源元数据未补全。");
        foreach(int index in Enumerable.Range(0,results!.Count))
        {
            var row=(FileRow)results[index]!;await results.EnsureLoaded(row,lifetime.Token);
            if(row.Kind!="image")continue;
            await SelectPreview(row);if(fitBitmap is null||selectedProperties?.RootId!=row.Item!.SourceRootId)throw new InvalidOperationException("收藏图片跨目录预览失败："+QualityLabel.Text);
        }
        var textIndex=Array.FindIndex(rows.ToArray(),i=>i.Kind=="text");await SelectPreview((FileRow)results[textIndex]!);
        if(displayedText?.Text.Contains("跨目录收藏文本验证")!=true)throw new InvalidOperationException("收藏文本预览错误。");
        await RefreshCurrentRoot();if(activeCollectionId!=collection.Id||results?.Count!=5)throw new InvalidOperationException("收藏夹 F5 刷新错误。");
        report["phase"]="excludeAndCategory";excludedCollectionIds=[excluded.Id];await ApplyBrowserFilters();if(results?.Count!=4)throw new InvalidOperationException("跨目录收藏排除失败。");
        SelectTag(Category,"presentations");await ApplyBrowserFilters();if(results?.Count!=1)throw new InvalidOperationException("收藏 PPT 分类失败。");
        var presentation=(FileRow)results[0]!;await results.EnsureLoaded(presentation,lifetime.Token);
        var target=await FolderLens.Infrastructure.ExternalFileLaunch.Resolve(catalog,prefetchSourceProbe,SourceRootPath(presentation),SourceRootId(presentation),presentation.Item!.EntryId,presentation.Item.Version,false,lifetime.Token);
        if(target.Path!=Path.Combine(b,"结案.pptx"))throw new InvalidOperationException("收藏文档的外部打开路径不正确。");
        await OpenCollection(collection.Id);await NavigateHistory(false); // Return to prior collection view with its filters.
        report["phase"]="normalDirectoryExclusion";ApplySavedFilter(new(){RootId=rootId,Kinds=[],ExcludeCollections=[excluded.Id]});await OpenRoot(b);await RefreshQuery();if(results?.Count!=2)throw new InvalidOperationException("普通目录排除收藏标签失败。");
        report["phase"]="knownMissing";await OpenCollection(collection.Id);
        File.Delete(Path.Combine(b,"结案.txt"));await RefreshCurrentRoot();
        rows=await catalog.ReadPage(resultHandle!.Id,0);textIndex=Array.FindIndex(rows.ToArray(),i=>i.Kind=="text");
        if(textIndex!=-1||results!.Count!=4)throw new InvalidOperationException("确认不存在的文件未从收藏中移除。");
        var choices=new ListView{ItemsSource=fileCollections,DisplayMemberPath="Name",SelectionMode=ListViewSelectionMode.Multiple,MaxHeight=240,MinWidth=380};
        var name=new TextBox{Header="或新建收藏夹",MaxLength=100,PlaceholderText="输入收藏夹名称"};
        var panel=CollectionSelectionPanel(choices,name,new TextBlock{TextWrapping=TextWrapping.Wrap},3);panel.Width=460;panel.Padding=new(12);panel.Background=(Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        Grid.SetRowSpan(panel,10);Grid.SetColumnSpan(panel,10);panel.HorizontalAlignment=HorizontalAlignment.Left;panel.VerticalAlignment=VerticalAlignment.Top;Shell.Children.Add(panel);
        try
        {
            Shell.UpdateLayout();await Task.Delay(50);choices.SelectedItems.Add(fileCollections.Single(c=>c.Id==collection.Id));choices.SelectedItems.Add(fileCollections.Single(c=>c.Id==excluded.Id));
            if(choices.SelectedItems.Count!=2||choices.ActualWidth<380||name.ActualHeight<30)throw new InvalidOperationException("原生收藏选择控件未正确布局或不能多选。");
            var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(panel);using var file=File.Create(Path.Combine(dataDirectory,"collection-selection.png"));using var stream=file.AsRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
        }
        finally{Shell.Children.Remove(panel);}
        await OpenCollection(collection.Id);await DeleteCollectionAndRefresh(collection.Id);
        if(activeCollectionId is not null||rootId.Length!=0||resultHandle is not null||selected is not null||RootPath.IsReadOnly||FilesGrid.Items.Count!=0)
            throw new InvalidOperationException("删除当前收藏夹后仍保留失效浏览页面。");
        if(!File.Exists(image)||!File.Exists(copy))throw new InvalidOperationException("删除收藏夹影响源文件。");
        report["deletedActiveCollectionReturnsToEmptyBrowser"]=true;
        report["crossRootPreview"]=true;report["batchSelection"]=true;report["textAndPresentationCategory"]=true;report["excludeInBothScopes"]=true;report["status"]="PASS";
    }
}
