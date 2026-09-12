namespace FolderLens.Core;

/// <summary>Bounded browser history. Callers provide the latest state of the page being left.</summary>
public sealed class NavigationHistory<T>
{
    private readonly List<T> back=[],forward=[];
    public bool CanGoBack=>back.Count>0;
    public bool CanGoForward=>forward.Count>0;
    public void VisitFrom(T current){Push(back,current);forward.Clear();}
    public T GoBack(T current)=>Move(back,forward,current);
    public T GoForward(T current)=>Move(forward,back,current);
    private static void Push(List<T> list,T value){if(list.Count==100)list.RemoveAt(0);list.Add(value);}
    private static T Move(List<T> source,List<T> destination,T current)
    {
        if(source.Count==0)throw new InvalidOperationException("没有可用的浏览历史。");
        T target=source[^1];source.RemoveAt(source.Count-1);Push(destination,current);return target;
    }
}
