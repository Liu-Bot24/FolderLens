using System.Text.Json;

namespace FolderLens.Infrastructure;

public sealed class AtomicSettings(string directory)
{
    private readonly SemaphoreSlim gate=new(1,1);
    // File I/O runs on the thread pool; callers are often on the UI thread.
    public async Task Save<T>(string name,T value,CancellationToken cancellation=default)
    {
        if(Path.GetFileName(name)!=name)throw new ArgumentException("Invalid settings filename.");
        await gate.WaitAsync(cancellation);
        try{await Task.Run(()=>SaveCore(name,value,cancellation),cancellation);}
        finally{gate.Release();}
    }
    public async Task Update<T>(string name,Func<T?,T> update,CancellationToken cancellation=default)
    {
        if(Path.GetFileName(name)!=name)throw new ArgumentException("Invalid settings filename.");
        await gate.WaitAsync(cancellation);
        try
        {
            var latest=await Task.Run(()=>LoadCore<T>(name,cancellation),cancellation);
            var next=update(latest);
            await Task.Run(()=>SaveCore(name,next,cancellation),cancellation);
        }
        finally{gate.Release();}
    }
    private async Task SaveCore<T>(string name,T value,CancellationToken cancellation)
    {
        string? temp=null;
        try
        {
            Directory.CreateDirectory(directory);string path=Path.Combine(directory,name);temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,64*1024,FileOptions.WriteThrough)){await JsonSerializer.SerializeAsync(stream,value,cancellationToken:cancellation);await stream.FlushAsync(cancellation);}
            cancellation.ThrowIfCancellationRequested();
            if(File.Exists(path))File.Replace(temp,path,path+".bak",true);else File.Move(temp,path);
        }
        finally{if(temp is not null&&File.Exists(temp))File.Delete(temp);}
    }
    public async Task<T?> Load<T>(string name,CancellationToken cancellation=default)
    {
        if(Path.GetFileName(name)!=name)throw new ArgumentException("Invalid settings filename.");
        await gate.WaitAsync(cancellation);
        try{return await Task.Run(()=>LoadCore<T>(name,cancellation),cancellation);}
        finally{gate.Release();}
    }
    private async Task<T?> LoadCore<T>(string name,CancellationToken cancellation)
    {
        string path=Path.Combine(directory,name);if(!File.Exists(path))return default;
        try{return await Read<T>(path,cancellation);}
        catch(JsonException) when(File.Exists(path+".bak"))
        {
            // A torn or corrupted current revision falls back to the previous
            // complete revision. If that is unreadable too, report the original.
            try{return await Read<T>(path+".bak",cancellation);}
            catch(JsonException){}
            throw;
        }
    }
    private static async Task<T?> Read<T>(string path,CancellationToken cancellation)
    {
        await using var stream=File.OpenRead(path);if(stream.Length>8*1024*1024)throw new InvalidDataException("设置文件超过允许大小。");
        return await JsonSerializer.DeserializeAsync<T>(stream,cancellationToken:cancellation);
    }
}
