using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFunctionalPhysical(string source,Dictionary<string,object> report)
    {
        byte[] sample=await File.ReadAllBytesAsync(Path.Combine(source,"A","image-00.png"));source=await CreateFunctionalViewerImages(Path.Combine(source,"physical"),2000,6000,1);
        for(int group=0;group<2;group++)
        {
            string directory=Path.Combine(source,$"Group{group}");Directory.CreateDirectory(directory);
            for(int i=0;i<30;i++)await File.WriteAllBytesAsync(Path.Combine(directory,$"image-{group*30+i:D2}.png"),sample);
        }
        folderGrouping=new(false);sortDescending=false;SelectTag(SortField,"name");await OpenRoot(source);await SettleFunctionalCollection();
        await catalog!.CreateCollection("Input A");await catalog.CreateCollection("Input B");await RefreshCollectionsTree();
        Title="FolderLens — Isolated functional input";AppWindow.IsShownInSwitchers=true;AppWindow.MoveAndResize(new(80,80,1500,1100));AppWindow.Show(false);
        string phase="GF";var observations=new List<object>();var assertions=new List<object>();var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[]? baseline=null;string? previousJson=null;bool writing=false;
        var panel=new StackPanel{Orientation=Orientation.Horizontal,Spacing=3,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray)};
        var label=new TextBlock{Text="隔离输入验收",Margin=new(4),VerticalAlignment=VerticalAlignment.Center};panel.Children.Add(label);
        async Task<object> Capture()
        {
            var items=resultHandle is null?[]:await FunctionalSelectedItems();
            return new{phase,details=DetailsMode.IsChecked==true,grouped=browserGroups is not null,sort=lastAppliedFilter?.Sort,entries=items.Select(item=>new{id=item.EntryId,path=item.RelativePath}).OrderBy(item=>item.id).ToArray(),
                canvasWidth=ImageCanvas.ActualWidth,canvasHeight=ImageCanvas.ActualHeight,sourceWidth,sourceHeight,intent=viewerScaleIntent.ToString(),scale=EffectiveScale(),panX=pan.X,panY=pan.Y,
                preview=selected?.RelativePath,immersive,previewLoading,queryBusy,root,collection=activeCollectionId,treeCollection=(FolderTree.SelectedNode?.Content as CollectionNode)?.Id};
        }
        async Task Observe()
        {
            if(writing||queryBusy||closing)return;writing=true;
            try
            {
                object snapshot=await Capture();string json=JsonSerializer.Serialize(snapshot);
                if(json!=previousJson){previousJson=json;observations.Add(snapshot);await File.WriteAllTextAsync(Path.Combine(dataDirectory,"physical-observations.json"),JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));}
                await File.WriteAllTextAsync(Path.Combine(dataDirectory,"physical-state.json"),json);
            }
            finally{writing=false;}
        }
        void Add(string text,Func<Task> action)
        {
            var button=new Button{Content=text,Padding=new(5,3,5,3)};panel.Children.Add(button);
            button.Click+=async(_,_)=>{try{await action();await Observe();}catch(Exception error){done.TrySetException(error);}};
        }
        foreach(var mode in new[]{("GF",false,false),("DF",true,false),("GG",false,true),("DG",true,true)})
            Add(mode.Item1,async()=>
            {
                await ReturnToBrowser();if(root!=source)await OpenRoot(source);phase=mode.Item1;baseline=null;
                folderGrouping=new(mode.Item3,"first","name","asc","all");ChooseBrowserView(mode.Item2?DetailsMode:ThumbnailMode,new RoutedEventArgs());await RefreshQuery();ActiveBrowser.SelectedIndex=-1;
                ActiveBrowser.ScrollIntoView(results![0],ScrollIntoViewAlignment.Leading);label.Text="隔离输入："+phase;
            });
        Add("记录选择",async()=>{baseline=(await FunctionalSelectedItems()).Select(item=>item.EntryId).Order().ToArray();if(baseline.Length<2)throw new InvalidOperationException("Physical selection requires multiple files.");label.Text="已记录 "+baseline.Length+" 项";});
        Add("检查排序",async()=>
        {
            if(queryBusy)await queryCompletion;var actual=(await FunctionalSelectedItems()).Select(item=>item.EntryId).Order().ToArray();
            bool same=baseline is not null&&baseline.SequenceEqual(actual);assertions.Add(new{phase,kind="identityAfterPhysicalSort",same,count=actual.Length});
            if(!same)throw new InvalidOperationException("Physical sorting changed the selection.");label.Text=phase+" 选择通过";
        });
        Add("长图",async()=>
        {
            phase="reading";await ReturnToBrowser();if(root!=source)await OpenRoot(source);
            long ordinal=await catalog.FindOrdinal(resultHandle!.Id,"photo-0.png")??throw new InvalidOperationException("Missing physical long image.");
            await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token);SetPreviewSplit(.3);label.Text="物理拖动检查";
        });
        Add("结束",()=>{done.TrySetResult();return Task.CompletedTask;});
        Grid.SetRow(panel,0);Grid.SetColumnSpan(panel,10);Shell.Children.Add(panel);Shell.UpdateLayout();
        var observer=DispatcherQueue.CreateTimer();observer.Interval=TimeSpan.FromMilliseconds(200);observer.Tick+=async(_,_)=>{try{await Observe();}catch(Exception error){done.TrySetException(error);}};observer.Start();
        try
        {
            await Observe();await done.Task.WaitAsync(TimeSpan.FromMinutes(15));observer.Stop();while(writing)await Task.Delay(10);
            report["physicalSortAssertions"]=assertions;report["observations"]=observations;
            if(assertions.Count<4)throw new InvalidOperationException("All four physical native list modes must be checked.");report["status"]="PASS";
        }
        finally{observer.Stop();Shell.Children.Remove(panel);}
    }
}
