using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace FolderLens.App;

// Supply WinUI's collection-view contract directly. The default grouped INCC
// adapter owns a second flattened cache which is unsafe around empty groups.
internal class BrowserVector(Func<int> count,Func<int,object> read,Func<object,int> locate):IObservableVector<object>
{
    public int Count=>count();
    public object this[int index]{get=>index>=0&&index<Count?read(index):throw new ArgumentOutOfRangeException(nameof(index));set=>throw new NotSupportedException();}
    public int IndexOf(object value){int index=locate(value);return index>=0&&index<Count?index:-1;}
    public bool Contains(object value)=>IndexOf(value)>=0;
    public bool IsReadOnly=>true;
    public event VectorChangedEventHandler<object>? VectorChanged;
    internal long ResetCount{get;private set;}
    internal void Changed(CollectionChange change,int index){if(change==CollectionChange.Reset)ResetCount++;VectorChanged?.Invoke(this,new VectorChange(change,checked((uint)index)));}
    private sealed class VectorChange(CollectionChange change,uint index):IVectorChangedEventArgs
    {public CollectionChange CollectionChange=>change;public uint Index=>index;}
    public IEnumerator<object> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public void CopyTo(object[] array,int index){for(int i=0;i<Count;i++)array[index+i]=this[i];}
    public void Add(object value)=>throw new NotSupportedException();
    public void Clear()=>throw new NotSupportedException();
    public void Insert(int index,object value)=>throw new NotSupportedException();
    public bool Remove(object value)=>throw new NotSupportedException();
    public void RemoveAt(int index)=>throw new NotSupportedException();
}

internal sealed class BrowserCollectionView:BrowserVector,ICollectionView,ICollectionViewFactory,IDisposable
{
    private sealed class State
    {
        public readonly List<GroupView> Groups=[];
        public int Count;
        public object Read(int index)
        {
            foreach(var group in Groups){if(index<group.GroupItems.Count)return group.GroupItems[index];index-=group.GroupItems.Count;}
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        public int Locate(object item)
        {
            int offset=0;foreach(var group in Groups){int index=group.GroupItems.IndexOf(item);if(index>=0)return offset+index;offset+=group.GroupItems.Count;}return -1;
        }
    }
    private sealed class GroupView:ICollectionViewGroup,IDisposable
    {
        private readonly IList items;
        private readonly BrowserVector vector;
        private readonly Action<GroupView,NotifyCollectionChangedEventArgs> changed;
        private int count;
        public GroupView(BrowserFileGroup group,Action<GroupView,NotifyCollectionChangedEventArgs> changed,bool empty=false)
        {
            Group=group;items=group.Items;this.changed=changed;
            count=empty?0:items.Count;
            vector=new(()=>count,i=>items[i]!,item=>{int index=items.IndexOf(item);return index<count?index:-1;});
            if(items is INotifyCollectionChanged observable)observable.CollectionChanged+=OnChanged;
        }
        public object Group{get;}
        public IObservableVector<object> GroupItems=>vector;
        private void OnChanged(object? sender,NotifyCollectionChangedEventArgs args)
        {
            count=items.Count;
            changed(this,args);
        }
        public void Notify(CollectionChange action,int index)=>vector.Changed(action,index);
        public void SetCount(int value)=>count=value;
        public void Dispose(){if(items is INotifyCollectionChanged observable)observable.CollectionChanged-=OnChanged;}
    }
    private readonly State state;
    private readonly BrowserVector groups;
    private ObservableCollection<BrowserFileGroup>? source;
    public BrowserCollectionView(ObservableCollection<BrowserFileGroup> source):this(new State(),source){}
    private BrowserCollectionView(State state,ObservableCollection<BrowserFileGroup> source):base(()=>state.Count,state.Read,state.Locate)
    {
        this.state=state;this.source=source;
        foreach(var group in source)state.Groups.Add(new(group,OnItemsChanged));
        state.Count=state.Groups.Sum(group=>group.GroupItems.Count);
        groups=new(()=>state.Groups.Count,i=>state.Groups[i],item=>item is GroupView group?state.Groups.IndexOf(group):-1);
        source.CollectionChanged+=OnGroupsChanged;
    }
    public object? Source{get=>source;set{if(value is not null)throw new NotSupportedException();Dispose();}}
    public ICollectionView View=>this;
    public ICollectionView CreateView()=>this;
    public IObservableVector<object> CollectionGroups=>groups;
    internal void ResetSource(ObservableCollection<BrowserFileGroup> next)
    {
        Dispose();state.Groups.Clear();source=next;
        foreach(var group in next)state.Groups.Add(new(group,OnItemsChanged));
        state.Count=state.Groups.Sum(group=>group.GroupItems.Count);
        source.CollectionChanged+=OnGroupsChanged;UpdateCurrency();
        groups.Changed(CollectionChange.Reset,0);Changed(CollectionChange.Reset,0);
    }
    private void OnItemsChanged(GroupView group,NotifyCollectionChangedEventArgs args)
    {
        if(args.Action==NotifyCollectionChangedAction.Reset)
        {state.Count=state.Groups.Sum(item=>item.GroupItems.Count);UpdateCurrency();group.Notify(CollectionChange.Reset,0);Changed(CollectionChange.Reset,0);return;}
        int offset=0;foreach(var candidate in state.Groups){if(ReferenceEquals(candidate,group))break;offset+=candidate.GroupItems.Count;}
        state.Count+=args.Action==NotifyCollectionChangedAction.Add?1:-1;
        UpdateCurrency();
        group.Notify(args.Action==NotifyCollectionChangedAction.Add?CollectionChange.ItemInserted:CollectionChange.ItemRemoved,
            args.Action==NotifyCollectionChangedAction.Add?args.NewStartingIndex:args.OldStartingIndex);
        Changed(args.Action==NotifyCollectionChangedAction.Add?CollectionChange.ItemInserted:CollectionChange.ItemRemoved,
            offset+(args.Action==NotifyCollectionChangedAction.Add?args.NewStartingIndex:args.OldStartingIndex));
    }
    private void OnGroupsChanged(object? sender,NotifyCollectionChangedEventArgs args)
    {
        if(args.Action==NotifyCollectionChangedAction.Remove)
        {
            var old=state.Groups[args.OldStartingIndex];
            if(old.GroupItems.Count>4096)
            {
                state.Groups.RemoveAt(args.OldStartingIndex);state.Count=state.Groups.Sum(group=>group.GroupItems.Count);
                UpdateCurrency();
                groups.Changed(CollectionChange.Reset,0);Changed(CollectionChange.Reset,0);old.Dispose();return;
            }
            int offset=state.Groups.Take(args.OldStartingIndex).Sum(group=>group.GroupItems.Count);
            for(int i=old.GroupItems.Count-1;i>=0;i--){state.Count--;old.SetCount(i);UpdateCurrency();old.Notify(CollectionChange.ItemRemoved,i);Changed(CollectionChange.ItemRemoved,offset+i);}
            state.Groups.RemoveAt(args.OldStartingIndex);
            groups.Changed(CollectionChange.ItemRemoved,args.OldStartingIndex);old.Dispose();
        }
        else if(args.Action==NotifyCollectionChangedAction.Add)
        {
            var group=(BrowserFileGroup)args.NewItems![0]!;
            if(group.Items.Count>4096)
            {
                // The WinRT vector protocol has no range event. Per-item events
                // force ListView to fetch every new object, even off screen.
                // Publish one coherent virtual index for a large structural edit.
                // The bound view, group models and retained FileRows stay owned.
                state.Groups.Insert(args.NewStartingIndex,new(group,OnItemsChanged));
                state.Count=state.Groups.Sum(item=>item.GroupItems.Count);
                UpdateCurrency();
                groups.Changed(CollectionChange.Reset,0);Changed(CollectionChange.Reset,0);return;
            }
            var added=new GroupView(group,OnItemsChanged,empty:true);
            state.Groups.Insert(args.NewStartingIndex,added);
            groups.Changed(CollectionChange.ItemInserted,args.NewStartingIndex);
            int offset=state.Groups.Take(args.NewStartingIndex).Sum(group=>group.GroupItems.Count);
            for(int i=0;i<group.Items.Count;i++){state.Count++;added.SetCount(i+1);UpdateCurrency();added.Notify(CollectionChange.ItemInserted,i);Changed(CollectionChange.ItemInserted,offset+i);}
        }
        else throw new InvalidOperationException("Grouped projection requires individual group insertions and removals.");
    }
    public int CurrentPosition{get;private set;}=-1;
    public object? CurrentItem{get;private set;}
    private bool afterLast;
    public bool IsCurrentBeforeFirst=>CurrentPosition<0;
    public bool IsCurrentAfterLast=>afterLast;
    public bool HasMoreItems=>false;
    public event EventHandler<object>? CurrentChanged;
    public event CurrentChangingEventHandler? CurrentChanging;
    private void UpdateCurrency()
    {
        int position=CurrentItem is {} item?IndexOf(item):afterLast?Count:-1;
        object? next=position>=0&&position<Count?this[position]:null;
        if(position==CurrentPosition&&ReferenceEquals(next,CurrentItem))return;
        // Structural edits cannot be cancelled; removal clears currency instead
        // of silently selecting the unrelated row which inherits its ordinal.
        CurrentChanging?.Invoke(this,new CurrentChangingEventArgs(false));
        CurrentPosition=position;CurrentItem=next;
        CurrentChanged?.Invoke(this,EventArgs.Empty);
    }
    public bool MoveCurrentToPosition(int index)
    {
        if(index< -1||index>Count)return false;
        var args=new CurrentChangingEventArgs();CurrentChanging?.Invoke(this,args);if(args.Cancel)return false;
        CurrentPosition=index;afterLast=index==Count;CurrentItem=index>=0&&index<Count?this[index]:null;
        CurrentChanged?.Invoke(this,EventArgs.Empty);return CurrentItem is not null;
    }
    public bool MoveCurrentTo(object item)=>MoveCurrentToPosition(IndexOf(item));
    public bool MoveCurrentToFirst()=>MoveCurrentToPosition(0);
    public bool MoveCurrentToLast()=>MoveCurrentToPosition(Count-1);
    public bool MoveCurrentToNext()=>MoveCurrentToPosition(Math.Min(Count,CurrentPosition+1));
    public bool MoveCurrentToPrevious()=>MoveCurrentToPosition(Math.Max(-1,CurrentPosition-1));
    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)=>Task.FromResult(new LoadMoreItemsResult{Count=0}).AsAsyncOperation();
    public void Dispose(){if(source is not null)source.CollectionChanged-=OnGroupsChanged;source=null;foreach(var group in state.Groups)group.Dispose();}
}
