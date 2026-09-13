using System.Collections.ObjectModel;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation.Collections;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private void VerifyCollectionContract(Dictionary<string,object> report)
    {
        var failures=new List<string>();int callbacks=0,structuralCurrencyChanges=0;
        using var initial=new VirtualResults(3,(_,_)=>throw new InvalidOperationException("No page read"));
        var a=(FileRow)initial[0]!;var b=(FileRow)initial[1]!;
        var first=new BrowserFileGroup(initial,new("a","A",0,2,0,2,"ready","all"));
        var second=new BrowserFileGroup(initial,new("b","B",0,1,2,1,"ready","all"));
        var source=new ObservableCollection<BrowserFileGroup>{first,second};using var view=new BrowserCollectionView(source);
        var subscribed=new HashSet<IObservableVector<object>>();
        void Check(IObservableVector<object> vector)
        {
            try{_ = vector[vector.Count];failures.Add("索引器允许访问Count之外");}catch(ArgumentOutOfRangeException){}
            try{_ = vector[-1];failures.Add("索引器允许负索引");}catch(ArgumentOutOfRangeException){}
            if(vector.Count<32)for(int i=0;i<vector.Count;i++)if(vector.IndexOf(vector[i])!=i)failures.Add("IndexOf不对应当前索引");
        }
        void Observe()
        {
            callbacks++;
            try
            {
                if(view.Count!=view.CollectionGroups.Cast<ICollectionViewGroup>().Sum(group=>group.GroupItems.Count))failures.Add("子组通知期间展平Count不一致");
                Check(view);Check(view.CollectionGroups);
                foreach(ICollectionViewGroup group in view.CollectionGroups)
                {
                    Check(group.GroupItems);
                    if(subscribed.Add(group.GroupItems))group.GroupItems.VectorChanged+=(_,_)=>Observe();
                }
            }
            catch(Exception error){failures.Add("回调读取失败："+error.GetType().Name);}
        }
        view.VectorChanged+=(_,_)=>Observe();view.CollectionGroups.VectorChanged+=(_,_)=>Observe();Observe();
        view.CurrentChanging+=(_,args)=>{if(!args.IsCancelable)structuralCurrencyChanges++;};
        view.MoveCurrentTo(a);
        using var smaller=new VirtualResults(1,(_,_)=>throw new InvalidOperationException());smaller.Retain(a,0,null);
        first.Update(smaller,first.Info with{Count=1},[new(1,1,0)]);
        using var inserted=new VirtualResults(2,(_,_)=>throw new InvalidOperationException());inserted.Retain(a,1,null);
        first.Update(inserted,first.Info with{Count=2},[new(0,0,1)]);
        if(!ReferenceEquals(view.CurrentItem,a)||view.CurrentPosition!=1)failures.Add("在当前项前插入后当前项身份改变");
        source.Add(new BrowserFileGroup(initial,new("c","C",0,1,1,1,"ready","all")));
        source.RemoveAt(2);
        source.Remove(first);
        if(view.CurrentItem is not null||view.CurrentPosition!=-1)failures.Add("删除当前项后仍指向其他文件");
        source.Insert(0,first);
        using var large=new VirtualResults(5000,(_,_)=>throw new InvalidOperationException());large.Retain(a,4000,null);
        view.MoveCurrentTo(a);
        first.Update(large,first.Info with{Count=5000},[new(0,2,5000)]);
        if(!ReferenceEquals(view.CurrentItem,a)||view.CurrentPosition!=4000)failures.Add("Reset后当前项身份改变");
        source.Remove(first);source.Add(first);
        if(structuralCurrencyChanges<3)failures.Add("结构变化缺少不可取消的 CurrentChanging 通知");
        report["collectionCallbacks"]=callbacks;report["structuralCurrencyChanges"]=structuralCurrencyChanges;report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException(string.Join("；",failures.Distinct()));
        report["status"]="PASS";
    }
}
