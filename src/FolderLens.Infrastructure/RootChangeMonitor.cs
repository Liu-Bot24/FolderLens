using FolderLens.Core;

namespace FolderLens.Infrastructure;

/// <summary>Bounded change hints, persistent directory reconciliation, and one watcher with bounded reconnect backoff.</summary>
public sealed class RootChangeMonitor : IDisposable
{
    private readonly string root;
    private readonly string[] ignoredDirectories;
    private readonly Action dirty;
    private readonly ScanDirtyDirectories? store;
    private readonly string? rootId;
    private readonly long epoch;
    private readonly Timer timer;
    private readonly CancellationTokenSource stop=new();
    private readonly object sync=new();
    private readonly Dictionary<string,DirectoryChangeHint> pending=new(StringComparer.Ordinal);
    private FileSystemWatcher? watcher;
    private bool reconnect=true;
    private int attempt,ticking,disposed,tokenDisposed;
    private long lastEvent,nextReconnect,lastTick=Environment.TickCount64,lastPeriodic=Environment.TickCount64,lastSignal;
    public string? LastError {get;private set;}
    public RootChangeMonitor(string root,Action dirty,CatalogStore? catalog=null,string? rootId=null,long epoch=0,string[]? ignoredDirectories=null)
    {
        this.root=PathRules.ValidateSource(root);this.dirty=dirty;this.rootId=rootId;this.epoch=epoch;
        this.ignoredDirectories=ignoredDirectories??[];
        if(catalog is not null&&rootId is not null)store=new(catalog);
        timer=new(Tick,null,0,500);
    }
    private void OnChange(object sender,FileSystemEventArgs e)
    {
        AddParent(e.FullPath,"FileChanged");if(e is RenamedEventArgs renamed)AddParent(renamed.OldFullPath,"Renamed");
    }
    private void AddParent(string path,string reason)
    {
        if(ignoredDirectories.Any(directory=>IsIgnoredPath(path,directory)))return;
        string relative=Path.GetRelativePath(root,Path.GetDirectoryName(path)??root);
        if(relative==".")relative="";
        if(relative==".."||relative.StartsWith("..\\",StringComparison.Ordinal))return;
        Add(new(relative,reason));
    }
    internal static bool IsIgnoredPath(string path,string directory)
    {
        string prefix=Path.GetFullPath(directory).TrimEnd('\\','/');string full=Path.GetFullPath(path);
        return full.Equals(prefix,StringComparison.OrdinalIgnoreCase)||full.StartsWith(prefix+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    public void MarkDirty()=>Add(new("","ExplicitReconcile",true));
    private void Add(DirectoryChangeHint hint)
    {
        lock(sync)
        {
            if(disposed!=0)return;
            lastEvent=Environment.TickCount64;
            if(pending.Count>=1024){pending.Clear();pending[""]=new("","EventBudgetOverflow",true);}
            else if(pending.TryGetValue(hint.RelativePath,out var old))pending[hint.RelativePath]=hint with{Subtree=hint.Subtree||old.Subtree};
            else pending[hint.RelativePath]=hint;
        }
    }
    private void OnError(object sender,ErrorEventArgs e)
    {
        lock(sync){reconnect=true;nextReconnect=Environment.TickCount64+5000;LastError="WatcherEventsLost";}
        Add(new("","WatcherEventsLost",true));
    }
    private async void Tick(object? state)
    {
        if(Interlocked.CompareExchange(ref ticking,1,0)!=0)return;
        try
        {
            if(Volatile.Read(ref disposed)!=0)return;
            long now=Environment.TickCount64;
            if(now-lastTick>45000){lock(sync){reconnect=true;nextReconnect=now;}Add(new("","ResumeReconcile",true));}
            lastTick=now;
            bool connect;lock(sync)connect=reconnect&&now>=nextReconnect;
            if(connect)await Reconnect().ConfigureAwait(false);
            if(store is not null&&now-lastPeriodic>=15*60*1000)
            {
                // Do not increment an already-pending root traversal: a long scan must not restart at zero every tick.
                await store.Mark(rootId!,epoch,[new("","PeriodicReconcile",true)],stop.Token,onlyIfMissing:true).ConfigureAwait(false);lastPeriodic=now;
            }
            DirectoryChangeHint[] batch;
            lock(sync)
            {
                batch=now-lastEvent>=300?pending.Values.Take(128).ToArray():[];
                foreach(var hint in batch)pending.Remove(hint.RelativePath);
            }
            if(batch.Length>0)
            {
                try
                {
                    if(store is not null)
                    {
                        var changes=batch.Where(h=>h.Reason!="WatcherUnavailable").ToArray();var unavailable=batch.Where(h=>h.Reason=="WatcherUnavailable").ToArray();
                        int saved=0;if(changes.Length>0)saved+=await store.Mark(rootId!,epoch,changes,stop.Token).ConfigureAwait(false);
                        if(unavailable.Length>0)saved+=await store.Mark(rootId!,epoch,unavailable,stop.Token,onlyIfMissing:true).ConfigureAwait(false);
                        if(saved<batch.Length)foreach(var hint in batch)Add(hint); // Initial scan may not have inserted a root directory yet.
                    }
                    if(store is null&&disposed==0)dirty();
                }
                catch{foreach(var hint in batch)Add(hint);throw;}
            }
            if(store is not null&&now-lastSignal>=1000&&(await store.Read(rootId!,epoch,stop.Token).ConfigureAwait(false)).Count>0&&disposed==0)
            {lastSignal=now;dirty();}
            else if(store is null&&now-lastPeriodic>=15*60*1000&&disposed==0){lastPeriodic=now;dirty();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){LastError=ex.GetType().Name;}
        finally{Interlocked.Exchange(ref ticking,0);if(Volatile.Read(ref disposed)==2)DisposeToken();}
    }
    private Task Reconnect()=>Task.Run(()=>
    {
        FileSystemWatcher? replacement=null;
        try
        {
            lock(sync){watcher?.Dispose();watcher=null;}
            replacement=new(root){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.Size|NotifyFilters.LastWrite|NotifyFilters.CreationTime,InternalBufferSize=32*1024};
            replacement.Created+=OnChange;replacement.Changed+=OnChange;replacement.Deleted+=OnChange;replacement.Renamed+=OnChange;replacement.Error+=OnError;replacement.EnableRaisingEvents=true;
            lock(sync)
            {
                if(disposed!=0){replacement.Dispose();return;}
                watcher=replacement;replacement=null;reconnect=false;attempt=0;LastError=null;
            }
            Add(new("","WatcherReconnected",true));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            replacement?.Dispose();lock(sync){LastError=ex is UnauthorizedAccessException?"WatcherAccessDenied":"WatcherOffline";int[] seconds=[5,15,60,300];nextReconnect=Environment.TickCount64+seconds[Math.Min(attempt++,3)]*1000L;reconnect=true;}
            Add(new("","WatcherUnavailable",true));
        }
    },stop.Token);
    public void Dispose()
    {
        if(Interlocked.CompareExchange(ref disposed,1,0)!=0)return;
        timer.Dispose();stop.Cancel();lock(sync){watcher?.Dispose();watcher=null;pending.Clear();}
        Volatile.Write(ref disposed,2);if(Volatile.Read(ref ticking)==0)DisposeToken();
    }
    private void DisposeToken(){if(Interlocked.Exchange(ref tokenDisposed,1)==0)stop.Dispose();}
}
