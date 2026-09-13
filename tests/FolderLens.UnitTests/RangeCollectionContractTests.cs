using FolderLens.Core;
using System.Collections.Specialized;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class RangeCollectionContractTests
{
    [Fact]
    public void BulkVirtualIndexReplacementDoesNotMaterializeRows()
    {
        int reads=0,events=0;
        var view=new VirtualRangeCollection<int>(12,index=>index,value=>value is int index?index:-1);
        view.CollectionChanged+=(_,args)=>{Assert.Equal(NotifyCollectionChangedAction.Reset,args.Action);Assert.Equal(1_000_000,view.Count);events++;};
        view.Replace(1_000_000,index=>{reads++;return index;},value=>value is int index?index:-1);
        Assert.Equal(1,events);Assert.Equal(0,reads);Assert.Equal(999_999,view[999_999]);Assert.Equal(1,reads);
        Assert.Throws<ArgumentOutOfRangeException>(()=>view.Replace(-1,index=>index,_=>-1));Assert.Equal(1_000_000,view.Count);
    }
    private sealed record Row(string Name,int Ordinal);
    [Fact]
    public void CompatibilityRemovalLocatorMatchesEveryIntermediateView()
    {
        string[] before=["A","B","C","D"],after=["C","D"];
        var view=new VirtualRangeCollection<string>(4,i=>before[i]);
        int calls=0;
        view.CollectionChanged+=(_,_)=>{calls++;for(int i=0;i<view.Count;i++)Assert.Equal(i,view.IndexOf(view[i]));Assert.Equal(-1,view.IndexOf("absent"));};
        view.Update(0,2,0,i=>after[i],value=>Array.IndexOf(after,value));
        Assert.Equal(2,calls);
    }
    [Fact]
    public void RangePayloadSurvivesRetiringItsSource()
    {
        bool retired=false;
        Row[] rows=Enumerable.Range(0,600).Select(i=>new Row($"row{i}",i)).ToArray();
        var view=new VirtualRangeCollection<Row>(rows.Length,i=>retired?new Row("retired",i):rows[i]);
        var notifications=new List<NotifyCollectionChangedEventArgs>();
        view.CollectionChanged+=(_,e)=>notifications.Add(e);
        view.UpdateRanges([new(0,600,0)],_=>throw new InvalidOperationException(),_=>-1);
        retired=true;
        var removed=notifications.SelectMany(e=>e.OldItems!.Cast<Row>()).ToArray();
        Assert.Equal(rows.Length,removed.Length);
        for(int i=0;i<rows.Length;i++)Assert.Same(rows[i],removed[i]);
    }

    [Fact]
    public void LargeRangeChangesPublishBoundedStablePayloadsAndValidIntermediateViews()
    {
        var old=Enumerable.Range(0,1800).Select(_=>new object()).ToArray();
        var next=old.Take(10).Concat(Enumerable.Range(0,1300).Select(_=>new object())).Concat(old.Skip(1610)).ToArray();
        var mirror=old.ToList();int calls=0;
        var view=new VirtualRangeCollection<object>(old.Length,i=>old[i],v=>Array.IndexOf(old,v));
        view.CollectionChanged+=(_,e)=>
        {
            calls++;var payload=e.OldItems??e.NewItems!;
            // WinUI's INCC-to-vector bridge emits only one change per event.
            Assert.Equal(1,payload.Count);
            if(e.Action==NotifyCollectionChangedAction.Remove)
            {
                Assert.Equal(mirror.Skip(e.OldStartingIndex).Take(payload.Count),payload.Cast<object>());
                mirror.RemoveRange(e.OldStartingIndex,payload.Count);
            }
            else mirror.InsertRange(e.NewStartingIndex,payload.Cast<object>());
            Assert.Equal(mirror,view.Cast<object>());
            Assert.Equal(-1,view.IndexOf(new object()));
            for(int i=0;i<view.Count;i++)Assert.Equal(i,view.IndexOf(view[i]));
        };
        view.UpdateRanges([new(10,1600,1300)],i=>next[i],v=>Array.IndexOf(next,v));
        Assert.True(calls>2);Assert.Equal(next,view.Cast<object>());
    }
    [Fact]
    public void RemovalAndAdditionExposeConsistentMembership()
    {
        Row[] old=[new("A",0),new("B",1),new("C",2)];Row[] next=[old[0],new("X",1),old[2]];
        var collection=new VirtualRangeCollection<Row>(3,index=>old[index],value=>value is Row row?row.Ordinal:-1);
        collection.CollectionChanged+=(_,change)=>
        {
            for(int index=0;index<collection.Count;index++)Assert.Equal(index,collection.IndexOf(collection[index]));
            Assert.False(collection.Contains(new Row("absent",0)));
            var payload=change.Action==NotifyCollectionChangedAction.Remove?change.OldItems!:change.NewItems!;
            Assert.True(payload.Contains(payload[0]));Assert.Equal(0,payload.IndexOf(payload[0]));
        };
        collection.Update(1,1,1,index=>next[index]);
    }
}
