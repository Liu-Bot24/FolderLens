using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record WorkerTemporaryCleanup(int RemovedInstances,int ActiveInstances,int UnownedInstances,int UnsafeEntries,long RetainedBytes);

/// <summary>Only nonce-named directories created with this ownership marker are
/// eligible. A held exclusive lock or a matching live process prevents cleanup.</summary>
internal static class WorkerTemporaryFiles
{
    private const string Marker=".folderlens-worker-owner.json",Lock=".folderlens-worker-active.lock";
    private sealed record Owner(int Schema,string Instance,int AppPid,long AppStartTicks,int? WorkerPid,long? WorkerStartTicks);
    internal sealed class Session : IDisposable
    {
        private readonly List<SafeFileHandle> pins;
        private SafeFileHandle? activeLock;
        private Owner owner;
        public string Path {get;}
        internal Session(string path,string instance,List<SafeFileHandle> pins)
        {
            Path=path;this.pins=pins;using var app=Process.GetCurrentProcess();owner=new(1,instance,app.Id,app.StartTime.ToUniversalTime().Ticks,null,null);
            activeLock=ThumbnailCache.PinOwnedFile(System.IO.Path.Combine(path,Lock),0xC0000080,1,0);WriteOwner();
        }
        private void WriteOwner()
        {
            string temporary=System.IO.Path.Combine(Path,".owner-"+Guid.NewGuid().ToString("N")+".tmp"),target=System.IO.Path.Combine(Path,Marker);
            File.WriteAllText(temporary,JsonSerializer.Serialize(owner));File.Move(temporary,target,true);
        }
        public void Bind(Process process){owner=owner with{WorkerPid=process.Id,WorkerStartTicks=process.StartTime.ToUniversalTime().Ticks};WriteOwner();}
        public void ReleaseActiveLock(){activeLock?.Dispose();activeLock=null;}
        public void ReleaseInstancePin(){if(pins.Count>0){pins[^1].Dispose();pins.RemoveAt(pins.Count-1);}}
        public long CheckBudget(long limit)
        {
            int visited=0,unsafeEntries=0;long bytes=Size(Path,ref visited,ref unsafeEntries,0);if(unsafeEntries>0)throw new IOException("工作进程临时目录包含不安全链接。");if(bytes>limit)throw new WorkerResourceLimitException("工作进程临时空间超过预算。");return bytes;
        }
        public void Dispose(){activeLock?.Dispose();activeLock=null;foreach(var pin in pins)pin.Dispose();pins.Clear();}
    }
    internal static Session Create(string root,string instance)
    {
        root=System.IO.Path.GetFullPath(root);if(root.StartsWith("\\",StringComparison.Ordinal))throw new IOException("工作进程临时目录必须位于本地磁盘。");
        var pins=new List<SafeFileHandle>();string current=System.IO.Path.GetPathRoot(root)!;
        try
        {
            pins.Add(ThumbnailCache.PinDirectory(current));
            foreach(string part in root[current.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries)){current=System.IO.Path.Combine(current,part);if(!Directory.Exists(current))Directory.CreateDirectory(current);pins.Add(ThumbnailCache.PinDirectory(current));}
            string path=System.IO.Path.Combine(root,instance);if(Directory.Exists(path))throw new IOException("工作进程实例目录已存在。");Directory.CreateDirectory(path);pins.Add(ThumbnailCache.PinDirectory(path));return new(path,instance,pins);
        }
        catch{foreach(var pin in pins)pin.Dispose();throw;}
    }
    internal static WorkerTemporaryCleanup Recover(string root)
    {
        root=System.IO.Path.GetFullPath(root);if(!Directory.Exists(root))return new(0,0,0,0,0);
        // Pin ancestors as well as the requested cleanup root; a lexical path check
        // alone cannot prevent swapping an ancestor for a junction.
        var pins=new List<SafeFileHandle>();int removed=0,active=0,unowned=0,unsafeEntries=0;long retained=0;
        try
        {
            string current=System.IO.Path.GetPathRoot(root)!;pins.Add(ThumbnailCache.PinDirectory(current));foreach(string part in root[current.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries)){current=System.IO.Path.Combine(current,part);pins.Add(ThumbnailCache.PinDirectory(current));}
            foreach(string directory in Directory.EnumerateDirectories(root,"*",SearchOption.TopDirectoryOnly).Take(1024))
            {
                if(!Guid.TryParseExact(System.IO.Path.GetFileName(directory),"N",out _)){unowned++;continue;}
                SafeFileHandle? pin=null,claim=null;
                try
                {
                    pin=ThumbnailCache.PinDirectory(directory);string marker=System.IO.Path.Combine(directory,Marker);if(!File.Exists(marker)){unowned++;continue;}
                    Owner? owner;using(var input=ThumbnailCache.OpenOwnedRead(marker)){if(input.Length>4096){unsafeEntries++;continue;}owner=JsonSerializer.Deserialize<Owner>(input);}
                    if(owner is not {Schema:1}||owner.Instance!=System.IO.Path.GetFileName(directory)){unsafeEntries++;continue;}
                    try{claim=ThumbnailCache.PinOwnedFile(System.IO.Path.Combine(directory,Lock),0xC0000080,3,0);}catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode is 32 or 33){active++;continue;}
                    if(owner.WorkerPid is {} worker&&Alive(worker,owner.WorkerStartTicks)){active++;continue;}
                    if(owner.WorkerPid is null&&Alive(owner.AppPid,owner.AppStartTicks)){active++;continue;}
                    claim.Dispose();claim=null;int visited=0;DeleteContents(directory,ref visited,ref unsafeEntries,0);pin.Dispose();pin=null;
                    if(!Directory.EnumerateFileSystemEntries(directory).Any()){Directory.Delete(directory,false);removed++;}else{visited=0;retained+=Size(directory,ref visited,ref unsafeEntries,0);}
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException){unsafeEntries++;}
                finally{claim?.Dispose();pin?.Dispose();}
            }
        }
        finally{foreach(var pin in pins)pin.Dispose();}
        return new(removed,active,unowned,unsafeEntries,retained);
    }
    internal static WorkerTemporaryCleanup ReleaseExited(Session? session)
    {
        if(session is null)return new(0,0,0,0,0);string directory=session.Path;int visited=0,unsafeEntries=0;session.ReleaseActiveLock();
        // The caller has already observed the process exit. This direct cleanup also
        // handles a launch failure before a worker PID could be written to the marker.
        try
        {
            DeleteContents(directory,ref visited,ref unsafeEntries,0);
            if(!Directory.EnumerateFileSystemEntries(directory).Any()){session.ReleaseInstancePin();Directory.Delete(directory,false);return new(1,0,0,unsafeEntries,0);}
            visited=0;return new(0,0,0,unsafeEntries,Size(directory,ref visited,ref unsafeEntries,0));
        }
        finally{session.Dispose();}
    }
    private static bool Alive(int pid,long? start)
    {
        try{using var process=Process.GetProcessById(pid);return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==start;}
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return true;}
    }
    private static long Size(string directory,ref int visited,ref int unsafeEntries,int depth)
    {
        if(depth>12)throw new IOException("临时目录层级超过清理预算。");long bytes=0;using var pin=ThumbnailCache.PinDirectory(directory);
        foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if(++visited>10000)throw new IOException("临时文件数量超过预算。");
            if(System.IO.Path.GetFileName(entry)==Lock)continue;
            try{if((File.GetAttributes(entry)&FileAttributes.Directory)!=0)bytes=checked(bytes+Size(entry,ref visited,ref unsafeEntries,depth+1));else bytes=checked(bytes+ThumbnailCache.OwnedFileLength(entry));}
            catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){unsafeEntries++;}
        }
        return bytes;
    }
    private static void DeleteContents(string directory,ref int visited,ref int unsafeEntries,int depth)
    {
        if(depth>12)throw new IOException("临时目录层级超过清理预算。");
        int failuresBefore=unsafeEntries;string[] paths=Directory.EnumerateFileSystemEntries(directory).Take(10001).ToArray();if(paths.Length>10000)throw new IOException("临时文件数量超过预算。");
        foreach(string entry in paths.OrderBy(p=>System.IO.Path.GetFileName(p) is Marker or Lock?1:0))
        {
            if(++visited>10000)throw new IOException("临时文件数量超过预算。");
            if(System.IO.Path.GetFileName(entry) is Marker or Lock&&unsafeEntries>failuresBefore)continue;
            try
            {
                if((File.GetAttributes(entry)&FileAttributes.Directory)!=0)
                {using(var pin=ThumbnailCache.PinDirectory(entry))DeleteContents(entry,ref visited,ref unsafeEntries,depth+1);if(!Directory.EnumerateFileSystemEntries(entry).Any())Directory.Delete(entry,false);}
                else ThumbnailCache.DeleteOwned(entry);
            }
            catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){unsafeEntries++;}
        }
    }
}
