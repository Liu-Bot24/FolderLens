namespace FolderLens.Core;

/// <summary>Bounded browser history. Callers provide the latest state of the page being left.</summary>
public sealed class NavigationHistory<T>
{
    private readonly List<T> back=[],forward=[];
    public bool CanGoBack=>back.Count>0;
    public bool CanGoForward=>forward.Count>0;
    public long Revision {get;private set;}
    public T PeekBack()=>Peek(back);
    public T PeekForward()=>Peek(forward);
    public void VisitFrom(T current){Push(back,current);forward.Clear();Revision++;}
    public T GoBack(T current)=>Move(back,forward,current);
    public T GoForward(T current)=>Move(forward,back,current);
    public T GoBackFromEmpty()=>Take(back);
    public T GoForwardFromEmpty()=>Take(forward);
    public void RemoveWhere(Predicate<T> invalid)
    {
        int removed=back.RemoveAll(invalid)+forward.RemoveAll(invalid);if(removed>0)Revision++;
    }
    private T Take(List<T> source)
    {
        T target=Peek(source);source.RemoveAt(source.Count-1);Revision++;return target;
    }
    private static void Push(List<T> list,T value){if(list.Count==100)list.RemoveAt(0);list.Add(value);}
    private static T Peek(List<T> source)=>source.Count>0?source[^1]:throw new InvalidOperationException("没有可用的浏览历史。");
    private T Move(List<T> source,List<T> destination,T current)
    {
        if(source.Count==0)throw new InvalidOperationException("没有可用的浏览历史。");
        T target=source[^1];source.RemoveAt(source.Count-1);Push(destination,current);Revision++;return target;
    }
}
