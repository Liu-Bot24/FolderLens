namespace FolderLens.Core;

/// <summary>Bounds in-flight work while choosing currently visible work ahead of
/// buffered work. Priority is re-evaluated on release, so scrolling can promote
/// an already queued item. Callers evaluate priority on their owning UI context.</summary>
public sealed class PriorityAdmissionGate(int concurrency)
{
    private readonly object sync=new();
    private readonly LinkedList<(Func<bool> Priority,TaskCompletionSource Ready)> waiting=new();
    private int available=concurrency>0?concurrency:throw new ArgumentOutOfRangeException(nameof(concurrency));
    public int CurrentCount { get { lock(sync)return available; } }
    public async Task WaitAsync(Func<bool> priority,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();TaskCompletionSource ready;
        lock(sync)
        {
            if(available>0){available--;return;}
            ready=new(TaskCreationOptions.RunContinuationsAsynchronously);waiting.AddLast((priority,ready));
        }
        using var registration=cancellation.Register(()=>ready.TrySetCanceled(cancellation));
        await ready.Task;
    }
    public void Release()
    {
        lock(sync)
        {
            while(waiting.Count>0)
            {
                LinkedListNode<(Func<bool> Priority,TaskCompletionSource Ready)>? chosen=null;
                for(var node=waiting.First;node is not null;)
                {
                    var next=node.Next;
                    if(node.Value.Ready.Task.IsCompleted)waiting.Remove(node);
                    else if(node.Value.Priority()){chosen=node;break;}
                    node=next;
                }
                chosen??=waiting.First;if(chosen is null)break;
                waiting.Remove(chosen);
                if(chosen.Value.Ready.TrySetResult())return;
            }
            if(available==concurrency)throw new SemaphoreFullException();
            available++;
        }
    }
}
