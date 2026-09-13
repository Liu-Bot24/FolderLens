using System.Collections;
using System.Collections.Specialized;

namespace FolderLens.Core;

public sealed record RangeEdit(int Prefix,int Removed,int Added);

/// <summary>A random-access view that reports edits without resetting its unchanged items.</summary>
public sealed class VirtualRangeCollection<T>(int count,Func<int,T> read,Func<object?,int>? locate=null):IList,INotifyCollectionChanged
{
    private Func<int,T> read=read;
    private Func<object?,int>? activeLocate=locate;
    public int Count {get;private set;}=count;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public void Replace(int count,Func<int,T> next,Func<object?,int> locate)
    {
        if(count<0)throw new ArgumentOutOfRangeException(nameof(count));
        Count=count;read=next;activeLocate=locate;
        CollectionChanged?.Invoke(this,new(NotifyCollectionChangedAction.Reset));
    }
    public void UpdateRanges(IReadOnlyList<RangeEdit> changes,Func<int,T> next,Func<object?,int> finalLocate,Func<object?,int>? originalLocate=null)
    {
        var original=read;int originalCount=Count,delta=0,end=0;
        originalLocate??=activeLocate;
        var finalStarts=new int[changes.Count];var finalEnds=new int[changes.Count];var shifts=new int[changes.Count];
        for(int i=0;i<changes.Count;i++)
        {
            var change=changes[i];if(change.Prefix<end||change.Removed<0||change.Added<0||change.Prefix+change.Removed>originalCount)throw new ArgumentOutOfRangeException(nameof(changes));
            finalStarts[i]=checked(change.Prefix+delta);finalEnds[i]=checked(finalStarts[i]+change.Added);delta=checked(delta+change.Added-change.Removed);shifts[i]=delta;end=change.Prefix+change.Removed;
        }
        int OriginalIndex(int index)
        {
            int low=0,high=finalStarts.Length-1;
            while(low<=high){int mid=low+(high-low)/2;if(finalStarts[mid]<=index)low=mid+1;else high=mid-1;}
            return high<0?index:index<finalEnds[high]?-1:index-shifts[high];
        }
        int stagePrefix=0,stageShift=0;
        T ReadStaged(int index)=>index<stagePrefix?next(index):original(index-stageShift);
        int LocateStaged(object? value)
        {
            int index=finalLocate(value),candidate=index;
            if(index>=stagePrefix){int old=OriginalIndex(index);candidate=old<0?-1:old+stageShift;}
            if(candidate>=0&&candidate<Count&&Equals(read(candidate),value))return candidate;
            // Items scheduled for a later removal still belong to this intermediate view.
            if(originalLocate is not null){int old=originalLocate(value);candidate=old+stageShift;if(old>=0&&candidate>=stagePrefix&&candidate<Count&&Equals(read(candidate),value))return candidate;}
            else for(int i=stagePrefix;i<Count;i++)if(Equals(read(i),value))return i;
            return -1;
        }
        // Subscribers observe the current projection through IList, never a stage delegate.
        // Keep one pair of delegates per update instead of allocating closures for every item.
        Func<int,T> stagedRead=ReadStaged;Func<object?,int> stagedLocate=LocateStaged;
        void Stage(int prefix,int shift)
        {
            stagePrefix=prefix;stageShift=shift;read=stagedRead;activeLocate=stagedLocate;
        }
        delta=0;
        void Notify(NotifyCollectionChangedEventArgs args)
        {
            try{CollectionChanged?.Invoke(this,args);}
            catch(Exception error)
            {
                error.Data["FolderLens.RangeNotification"]=$"action={args.Action}; originalCount={originalCount}; currentCount={Count}; oldIndex={args.OldStartingIndex}; newIndex={args.NewStartingIndex}; removed={args.OldItems?.Count??0}; added={args.NewItems?.Count??0}; edits={string.Join(",",changes.Take(128))}";
                throw;
            }
        }
        foreach(var change in changes)
        {
            int position=change.Prefix+delta;
            // WinUI's BindableObservableVectorWrapper translates each INCC Add/Remove
            // into ONE vector change, regardless of payload.Count. Multi-item events
            // silently desynchronize its item cache and later removals fail with E_INVALIDARG.
            // Publish one immutable item and its matching intermediate Count per event.
            for(int removed=0;removed<change.Removed;)
            {
                const int length=1;
                var items=new T[length];for(int i=0;i<length;i++)items[i]=original(change.Prefix+removed+i);
                Count-=length;delta-=length;Stage(position,delta);removed+=length;
                Notify(new(NotifyCollectionChangedAction.Remove,items,position));
            }
            for(int added=0;added<change.Added;)
            {
                const int length=1;int start=position+added;
                var items=new T[length];for(int i=0;i<length;i++)items[i]=next(start+i);
                Count+=length;delta+=length;added+=length;Stage(position+added,delta);
                Notify(new(NotifyCollectionChangedAction.Add,items,start));
            }
        }
        read=next;activeLocate=finalLocate;
    }
    public void Update(int prefix,int removed,int added,Func<int,T> next,Func<object?,int>? locateDuringRemoval=null)
    {
        if(prefix<0||removed<0||added<0||prefix+removed>Count)throw new ArgumentOutOfRangeException(nameof(prefix));
        var previous=read;
        for(int deleted=0;deleted<removed;)
        {
            T[] items=[previous(prefix+deleted)];int completed=++deleted;
            // The supplied locator describes the complete removal, not each partial
            // deletion. Search the current projection until that stage is reached.
            read=index=>previous(index<prefix?index:index+completed);Count--;activeLocate=completed==removed?locateDuringRemoval:null;
            CollectionChanged?.Invoke(this,new(NotifyCollectionChangedAction.Remove,items,prefix));
        }
        for(int inserted=0;inserted<added;)
        {
            T[] items=[next(prefix+inserted)];int position=prefix+inserted,completed=++inserted;
            read=index=>index<prefix+completed?next(index):previous(index-completed+removed);Count=checked(Count+1);activeLocate=null;
            CollectionChanged?.Invoke(this,new(NotifyCollectionChangedAction.Add,items,position));
        }
        read=next;activeLocate=locate;
    }
    public object? this[int index]{get=>index>=0&&index<Count?read(index):throw new ArgumentOutOfRangeException(nameof(index));set=>throw new NotSupportedException();}
    public int IndexOf(object? value){if(activeLocate is not null){int index=activeLocate(value);return index>=0&&index<Count&&Equals(this[index],value)?index:-1;}for(int index=0;index<Count;index++)if(Equals(this[index],value))return index;return -1;}
    public bool Contains(object? value)=>IndexOf(value)>=0;
    public IEnumerator GetEnumerator(){for(int index=0;index<Count;index++)yield return this[index];}
    public void CopyTo(Array array,int index){for(int offset=0;offset<Count;offset++)array.SetValue(this[offset],index+offset);}
    public bool IsReadOnly=>true;public bool IsFixedSize=>false;public bool IsSynchronized=>false;public object SyncRoot=>this;
    public int Add(object? value)=>throw new NotSupportedException();public void Clear()=>throw new NotSupportedException();public void Insert(int index,object? value)=>throw new NotSupportedException();public void Remove(object? value)=>throw new NotSupportedException();public void RemoveAt(int index)=>throw new NotSupportedException();
}
