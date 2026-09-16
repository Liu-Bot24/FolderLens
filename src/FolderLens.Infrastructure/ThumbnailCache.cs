using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record ThumbnailCacheKey(string EntryId,long FileVersion,long ModifiedUtcTicks,long LogicalBytes,int Edge,string ProviderVersion,string Representation="thumbnail",string OrientationPolicy="exif-v1",string ColorPolicy="srgb-v1",string SourceSignature="")
{
    public string Hash()
    {
        if(string.IsNullOrWhiteSpace(EntryId)||EntryId.Length>512||FileVersion<1||ModifiedUtcTicks<0||LogicalBytes<0||Edge is not(256 or 512 or 1024)||string.IsNullOrWhiteSpace(ProviderVersion)||ProviderVersion.Length>512||Representation is not("thumbnail" or "rawEmbedded" or "videoCover" or "audioCover")||OrientationPolicy.Length>128||ColorPolicy.Length>128)throw new ArgumentException("缩略图缓存键无效。");
        using var bytes=new MemoryStream();using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true)){writer.Write(1);writer.Write(EntryId);writer.Write(FileVersion);writer.Write(ModifiedUtcTicks);writer.Write(LogicalBytes);writer.Write(Edge);writer.Write(ProviderVersion);writer.Write(Representation);writer.Write(OrientationPolicy);writer.Write(ColorPolicy);}
        using(var signature=new BinaryWriter(bytes,Encoding.UTF8,true))signature.Write(SourceSignature);
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0,checked((int)bytes.Length))));
    }
}
public sealed record ThumbnailCacheOptions
{
    public long MemoryBytes {get;init;}=64L<<20;
    public int MemoryEntries {get;init;}=2048;
    public long DiskBytes {get;init;}=256L<<20;
    public int DiskEntries {get;init;}=4096;
    public long MaxEntryBytes {get;init;}=16L<<20;
    public TimeSpan MaxAge {get;init;}=TimeSpan.FromDays(7);
    public long MinimumFreeBytes {get;init;}=5L<<30;
}
public sealed record ThumbnailCacheStatistics(long DiskEntries,long PayloadBytes,long TotalDiskBytes,long MemoryBytes,int MemoryEntries,long Hits,long Misses,long Stores,long Evictions,int ActiveLeases);
public sealed record ThumbnailCacheCleanup(long RemovedEntries,long RemovedBytes,int ActiveLeases);
public sealed record ThumbnailCacheWrite(ThumbnailCacheLease? Lease,string? Warning);

/// <summary>The lease protects an encoded PNG from LRU cleanup. Dispose after XAML
/// has loaded it; the memory cache below accounts encoded bytes, not UI/GPU bitmaps.</summary>
public sealed class ThumbnailCacheLease : IDisposable
{
    private Action? release;
    private readonly SafeFileHandle pin;
    internal ThumbnailCacheLease(string path,long length,SafeFileHandle pin,Action release){Path=path;Length=length;this.pin=pin;this.release=release;}
    public string Path {get;}
    public long Length {get;}
    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(release is null,this);
        return ThumbnailCache.OpenOwnedRead(Path);
    }
    public void Dispose(){var action=Interlocked.Exchange(ref release,null);if(action is null)return;pin.Dispose();action();}
}

/// <summary>Flat, application-owned persistent PNG cache with a SQLite LRU index.
/// All filesystem/index work runs on its bounded database executor, never the UI.</summary>
public sealed class ThumbnailCache : IAsyncDisposable
{
    private const string Marker=".folderlens-thumbnail-cache-v1";
    private readonly string directory;
    private readonly ThumbnailCacheOptions options;
    private readonly SemaphoreSlim initialization=new(1,1);
    private DatabaseExecutor? executor;
    private readonly List<SafeFileHandle> directoryPins=[];
    private SafeFileHandle? ownerLock;
    private readonly Dictionary<string,LinkedListNode<(string Key,byte[] Bytes)>> memory=[];
    private readonly LinkedList<(string Key,byte[] Bytes)> recent=[];
    private readonly Dictionary<string,int> leases=[];
    private readonly HashSet<string> rejected=[];
    private readonly object leaseGate=new();
    private long bytes,entries,memoryBytes,hits,misses,stores,evictions,otherDiskBytes;
    private DateTime lastMaintenance=DateTime.MinValue;
    private bool disposed;
    private int pressureTrim;
    public ThumbnailCache(string directory,ThumbnailCacheOptions? options=null)
    {
        this.directory=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);this.options=options??new();
        if(this.directory.StartsWith("\\",StringComparison.Ordinal)||this.directory==Path.GetPathRoot(this.directory)?.TrimEnd('\\')||this.options.MemoryBytes<0||this.options.MemoryEntries<1||this.options.DiskBytes<1<<20||this.options.MaxEntryBytes<24||this.options.MaxEntryBytes>64L<<20||this.options.MaxAge<=TimeSpan.Zero||this.options.MinimumFreeBytes<0)throw new ArgumentException("缩略图缓存位置或预算无效。");
        WorkerResources.Shared.MemoryPressure+=OnMemoryPressure;
    }
    public static int SelectEdge(double physicalPixels)=>physicalPixels>512?1024:physicalPixels>256?512:256;
    public async Task Initialize(CancellationToken cancellation=default)
    {
        await initialization.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(executor is not null)return;
            await Task.Run(()=>PrepareDirectory(),cancellation).ConfigureAwait(false);
            var db=new DatabaseExecutor(Path.Combine(directory,"index.sqlite"));
            try
            {
                await db.Execute(c=>
                {
                    using var cmd=c.CreateCommand();cmd.CommandText="""
                        CREATE TABLE IF NOT EXISTS ThumbnailItems(cache_key TEXT PRIMARY KEY,byte_count INTEGER NOT NULL CHECK(byte_count>0),last_access INTEGER NOT NULL,created INTEGER NOT NULL) STRICT;
                        CREATE INDEX IF NOT EXISTS IX_ThumbnailLRU ON ThumbnailItems(last_access,cache_key);
                        SELECT COALESCE(sum(byte_count),0),count(*) FROM ThumbnailItems;
                        """;
                    using(var rows=cmd.ExecuteReader()){rows.Read();bytes=rows.GetInt64(0);entries=rows.GetInt64(1);}
                    RecoverOrphans(c,cancellation);return true;
                },cancellation).ConfigureAwait(false);
                executor=db;
            }
            catch{await db.DisposeAsync().ConfigureAwait(false);throw;}
        }
        finally{initialization.Release();}
        await Trim(cancellation).ConfigureAwait(false);
    }
    private void PrepareDirectory()
    {
        if(new DriveInfo(Path.GetPathRoot(directory)!).DriveType==DriveType.Network)throw new IOException("缓存必须位于本地磁盘。");
        // Pin every ancestor without FILE_SHARE_DELETE before accepting/creating the
        // owned directory. A junction cannot be swapped underneath later file opens.
        string current=Path.GetPathRoot(directory)!;
        try
        {
            directoryPins.Add(PinDirectory(current));
            foreach(string part in directory[Path.GetPathRoot(directory)!.Length..].Split('\\',StringSplitOptions.RemoveEmptyEntries))
            {
                current=Path.Combine(current,part);if(!Directory.Exists(current))Directory.CreateDirectory(current);directoryPins.Add(PinDirectory(current));
            }
            string marker=Path.Combine(directory,Marker);
            if(!File.Exists(marker))
            {
                if(Directory.EnumerateFileSystemEntries(directory).Any())throw new IOException("请选择空的应用缓存目录，不能清理已有文件目录。");
                using var output=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write("FolderLens thumbnail cache v1\n"u8);output.Flush(true);
            }
            using(var input=OpenOwnedRead(marker)){if(input.Length!=30)throw new IOException("缓存所有权标记无效。");using var reader=new StreamReader(input);if(reader.ReadToEnd()!="FolderLens thumbnail cache v1\n")throw new IOException("缓存所有权标记无效。");}
            ownerLock=Pin(Path.Combine(directory,".folderlens-owner.lock"),0xC0000080,false,4,0);
            foreach(string name in new[]{"index.sqlite","index.sqlite-wal","index.sqlite-shm"})if(File.Exists(Path.Combine(directory,name)))using(OpenOwnedRead(Path.Combine(directory,name))){}
        }
        catch{ownerLock?.Dispose();ownerLock=null;foreach(var pin in directoryPins)pin.Dispose();directoryPins.Clear();throw;}
    }
    private DatabaseExecutor Db(){ObjectDisposedException.ThrowIf(disposed,this);return executor??throw new InvalidOperationException("请先初始化缩略图缓存。");}
    private void OnMemoryPressure()
    {
        if(disposed||executor is null||Interlocked.Exchange(ref pressureTrim,1)!=0)return;
        _=ReleaseMemoryOnPressure();
    }
    private async Task ReleaseMemoryOnPressure()
    {
        try{await executor!.Execute(c=>{memory.Clear();recent.Clear();memoryBytes=0;return true;}).ConfigureAwait(false);}
        catch(System.Threading.Channels.ChannelClosedException){}
        catch(ObjectDisposedException){}
        finally{Volatile.Write(ref pressureTrim,0);}
    }
    private void RecoverOrphans(SqliteConnection c,CancellationToken cancellation)
    {
        // The exclusive cache owner lock and serialized writer prove that no
        // previous process can still be writing these staging files.
        foreach(string file in Directory.EnumerateFiles(directory,".*.tmp"))
        {
            cancellation.ThrowIfCancellationRequested();
            string name=Path.GetFileName(file);
            if(name.Length==102&&name[65]=='.'&&name.AsSpan(1,64).ToString().All(Uri.IsHexDigit)&&Guid.TryParseExact(name.Substring(66,32),"N",out _))DeleteOwned(file);
        }
        using var query=c.CreateCommand();query.CommandText="SELECT 1 FROM ThumbnailItems WHERE cache_key=$key";var key=query.Parameters.Add("$key",SqliteType.Text);
        foreach(string file in Directory.EnumerateFiles(directory,"*.png",SearchOption.TopDirectoryOnly))
        {
            cancellation.ThrowIfCancellationRequested();string hash=Path.GetFileNameWithoutExtension(file);if(hash.Length!=64||!hash.All(Uri.IsHexDigit))continue;
            key.Value=hash;if(query.ExecuteScalar() is null)DeleteOwned(file);
        }
        otherDiskBytes=Math.Max(0,Directory.EnumerateFiles(directory).Sum(p=>new FileInfo(p).Length)-bytes-IndexBytes());
    }
    private string AssetPath(string hash)
    {
        if(hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new InvalidDataException("缓存索引包含无效键。");
        return Path.Combine(directory,hash+".png");
    }
    public Task<ThumbnailCacheLease?> TryGet(ThumbnailCacheKey key,CancellationToken cancellation=default)
    {
        string hash=key.Hash();return Db().Execute(c=>Get(c,hash,key.Edge),cancellation);
    }
    private ThumbnailCacheLease? Get(SqliteConnection c,string hash,int edge)
    {
        Maintain(c);
        using var cmd=c.CreateCommand();cmd.CommandText="SELECT byte_count,last_access FROM ThumbnailItems WHERE cache_key=$key";cmd.Parameters.AddWithValue("$key",hash);
        long size,last;using(var row=cmd.ExecuteReader()){if(!row.Read()){misses++;return null;}size=row.GetInt64(0);last=row.GetInt64(1);}
        if(rejected.Contains(hash)){Remove(c,hash,size);misses++;return null;}
        if(last<DateTime.UtcNow.Subtract(options.MaxAge).Ticks&&!IsLeased(hash)){Remove(c,hash,size);misses++;return null;}
        string path=AssetPath(hash);SafeFileHandle? pin=null;
        try
        {
            pin=PinFile(path,0x80000000);using(var input=OpenOwnedRead(path)){if(input.Length!=size||size>options.MaxEntryBytes)throw new InvalidDataException("缓存长度已变化或超预算。");ValidatePng(input,edge);}
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException)
        {
            pin?.Dispose();Remove(c,hash,size);misses++;return null;
        }
        UpdateAccess(c,hash);hits++;
        lock(leaseGate){leases.TryGetValue(hash,out int count);leases[hash]=count+1;}
        return new(path,size,pin!,()=>{lock(leaseGate){if(leases.TryGetValue(hash,out int count)){if(count<=1)leases.Remove(hash);else leases[hash]=count-1;}}});
    }
    public async Task<ThumbnailCacheWrite> StoreOptional(ThumbnailCacheKey key,string encodedPngPath,CancellationToken cancellation=default)
    {
        try{return new(await Store(key,encodedPngPath,cancellation).ConfigureAwait(false),null);}
        catch(Exception error) when(error is IOException and not FileNotFoundException and not DirectoryNotFoundException
            or UnauthorizedAccessException or SqliteException {SqliteErrorCode:8 or 10 or 13 or 14})
        {
            cancellation.ThrowIfCancellationRequested();
            return new(null,"缩略图缓存未写入："+error.Message);
        }
    }
    public Task<ThumbnailCacheLease> Store(ThumbnailCacheKey key,string encodedPngPath,CancellationToken cancellation=default)
    {
        string hash=key.Hash();return Db().Execute(c=>
        {
            using var source=new FileStream(encodedPngPath,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,FileOptions.SequentialScan);
            ValidatePng(source,key.Edge);if(source.Length>options.MaxEntryBytes)throw new IOException("缩略图超过单项缓存预算。");source.Position=0;
            var existing=Get(c,hash,key.Edge);if(existing is not null)return existing;
            if(IsLeased(hash))throw new IOException("失效缩略图仍在释放，暂不写入缓存。");
            long size=source.Length;EnsureRoom(c,size,cancellation);
            string temporary=Path.Combine(directory,"."+hash+"."+Guid.NewGuid().ToString("N")+".tmp"),target=AssetPath(hash);
            bool recorded=false,moved=false;
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,64*1024,FileOptions.WriteThrough))
                {
                    byte[] buffer=new byte[64*1024];int count;long copied=0;
                    while((count=source.Read(buffer))>0){cancellation.ThrowIfCancellationRequested();copied=checked(copied+count);if(copied>size)throw new InvalidDataException("缩略图在缓存过程中发生变化。");output.Write(buffer,0,count);}
                    if(copied!=size)throw new InvalidDataException("缩略图在缓存过程中发生变化。");output.Flush(true);
                }
                cancellation.ThrowIfCancellationRequested();
                if(File.Exists(target))DeleteOwned(target);File.Move(temporary,target);moved=true;
                using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO ThumbnailItems(cache_key,byte_count,last_access,created) VALUES($key,$size,$now,$now)";cmd.Parameters.AddWithValue("$key",hash);cmd.Parameters.AddWithValue("$size",size);cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);cmd.ExecuteNonQuery();recorded=true;bytes+=size;entries++;stores++;
                return Get(c,hash,key.Edge)??throw new IOException("无法重新读取写入的缩略图。");
            }
            finally {if(File.Exists(temporary))DeleteOwned(temporary);if(moved&&!recorded&&File.Exists(target))DeleteOwned(target);}
        },cancellation);
    }
    public Task<ReadOnlyMemory<byte>?> GetBytes(ThumbnailCacheKey key,CancellationToken cancellation=default)
    {
        string hash=key.Hash();return Db().Execute<ReadOnlyMemory<byte>?>(c=>
        {
            Maintain(c);
            if(memory.TryGetValue(hash,out var hit)){recent.Remove(hit);recent.AddLast(hit);UpdateAccess(c,hash);hits++;return hit.Value.Bytes;}
            using var lease=Get(c,hash,key.Edge);if(lease is null)return null;
            using var input=lease.OpenRead();byte[] buffer=new byte[checked((int)input.Length)];input.ReadExactly(buffer);
            if(buffer.Length<=options.MemoryBytes)
            {
                while(recent.First is {} first&&(memoryBytes+buffer.Length>options.MemoryBytes||memory.Count>=options.MemoryEntries))RemoveMemory(first.Value.Key);
                var node=recent.AddLast((hash,buffer));memory.Add(hash,node);memoryBytes+=buffer.Length;
            }
            return buffer;
        },cancellation);
    }
    private void UpdateAccess(SqliteConnection c,string hash)
    {
        using var cmd=c.CreateCommand();cmd.CommandText="UPDATE ThumbnailItems SET last_access=$now WHERE cache_key=$key";cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.Ticks);cmd.Parameters.AddWithValue("$key",hash);cmd.ExecuteNonQuery();BoundJournal(c);
    }
    private bool IsLeased(string hash){lock(leaseGate)return leases.ContainsKey(hash);}
    private int ActiveLeases(){lock(leaseGate)return leases.Values.Sum();}
    private void RemoveMemory(string hash){if(memory.Remove(hash,out var item)){memoryBytes-=item.Value.Bytes.Length;recent.Remove(item);}}
    private bool Remove(SqliteConnection c,string hash,long size)
    {
        if(IsLeased(hash))return false;
        try{string path=AssetPath(hash);if(File.Exists(path))DeleteOwned(path);}
        catch(IOException){return false;}catch(Win32Exception){return false;}
        using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM ThumbnailItems WHERE cache_key=$key";cmd.Parameters.AddWithValue("$key",hash);
        if(cmd.ExecuteNonQuery()!=0){bytes-=size;entries--;evictions++;}rejected.Remove(hash);RemoveMemory(hash);BoundJournal(c);return true;
    }
    private void EnsureRoom(SqliteConnection c,long incoming,CancellationToken cancellation)
    {
        if(options.DiskEntries<1)throw new ArgumentException("缩略图数量预算无效。");
        while(entries>=options.DiskEntries)
        {
            using var oldest=c.CreateCommand();oldest.CommandText="SELECT cache_key,byte_count FROM ThumbnailItems ORDER BY last_access,cache_key";
            var candidates=new List<(string Key,long Bytes)>();using(var rows=oldest.ExecuteReader())while(rows.Read()){cancellation.ThrowIfCancellationRequested();if(!IsLeased(rows.GetString(0))){candidates.Add((rows.GetString(0),rows.GetInt64(1)));break;}}
            if(candidates.Count==0||!Remove(c,candidates[0].Key,candidates[0].Bytes))throw new IOException("缩略图数量已达到预算，正在显示的缩略图已保留。");
        }
        long reserve=IndexBytes()+otherDiskBytes+Math.Min(8L<<20,options.DiskBytes/8);
        while(bytes+incoming>options.DiskBytes-reserve)
        {
            long before=bytes;TrimCore(c,false,incoming,cancellation);if(bytes>=before)throw new IOException("缩略图缓存空间不足，正在显示的缓存已保留。");
        }
        var drive=new DriveInfo(Path.GetPathRoot(directory)!);long floor=Math.Max(options.MinimumFreeBytes,Math.Min(20L<<30,(long)(drive.TotalSize*.02)));
        if(drive.AvailableFreeSpace-incoming<floor)throw new IOException("缓存磁盘剩余空间不足。");
    }
    public Task<ThumbnailCacheCleanup> Trim(CancellationToken cancellation=default)=>Db().Execute(c=>TrimCore(c,false,0,cancellation),cancellation);
    public Task<ThumbnailCacheCleanup> Clear(CancellationToken cancellation=default)=>Db().Execute(c=>TrimCore(c,true,0,cancellation),cancellation);
    public Task<bool> Invalidate(ThumbnailCacheKey key,CancellationToken cancellation=default)
    {
        string hash=key.Hash();return Db().Execute(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT byte_count FROM ThumbnailItems WHERE cache_key=$key";cmd.Parameters.AddWithValue("$key",hash);object? size=cmd.ExecuteScalar();if(size is not long length)return false;rejected.Add(hash);RemoveMemory(hash);return Remove(c,hash,length);},cancellation);
    }
    public async Task<T?> TryLoad<T>(ThumbnailCacheKey key,Func<ThumbnailCacheLease,Task<T>> decode,CancellationToken cancellation=default) where T:class
    {
        using(var lease=await TryGet(key,cancellation))
        {
            if(lease is null)return null;
            try{return await decode(lease);}
            catch(COMException error) when(error.HResult is unchecked((int)0x88982F07) or unchecked((int)0x88982F60) or unchecked((int)0x88982F61) or unchecked((int)0x88982F62))
            { /* WIC rejected encoded PNG content; release the pin before invalidating. */ }
        }
        await Invalidate(key,cancellation);return null;
    }
    private ThumbnailCacheCleanup TrimCore(SqliteConnection c,bool clear,long incoming,CancellationToken cancellation)
    {
        long removed=0,removedBytes=0;long cutoff=DateTime.UtcNow.Subtract(options.MaxAge).Ticks;long target=options.DiskBytes-IndexBytes()-otherDiskBytes-Math.Min(8L<<20,options.DiskBytes/8)-incoming;
        long afterTime=-1;string afterKey="";
        while(true)
        {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT cache_key,byte_count,last_access FROM ThumbnailItems WHERE last_access>$time OR (last_access=$time AND cache_key>$key) ORDER BY last_access,cache_key LIMIT 256";cmd.Parameters.AddWithValue("$time",afterTime);cmd.Parameters.AddWithValue("$key",afterKey);
            var page=new List<(string Hash,long Size,long Access)>();using(var rows=cmd.ExecuteReader())while(rows.Read())page.Add((rows.GetString(0),rows.GetInt64(1),rows.GetInt64(2)));
            if(page.Count==0)break;
            bool finished=false;
            foreach(var item in page)
            {
                cancellation.ThrowIfCancellationRequested();afterTime=item.Access;afterKey=item.Hash;
                if(!clear&&item.Access>=cutoff&&bytes<=target&&entries<=options.DiskEntries){finished=true;break;}
                if(Remove(c,item.Hash,item.Size)){removed++;removedBytes+=item.Size;}
            }
            if(finished)break;
        }
        // Crash-orphaned temporary assets are never treated as source directories.
        foreach(string temp in Directory.EnumerateFiles(directory,".*.tmp",SearchOption.TopDirectoryOnly))
        {
            cancellation.ThrowIfCancellationRequested();string name=Path.GetFileName(temp);if(name.Length!=102||!name.AsSpan(1,64).ToString().All(Uri.IsHexDigit)||!name.AsSpan(66,32).ToString().All(Uri.IsHexDigit))continue;
            if(File.GetLastWriteTimeUtc(temp)<DateTime.UtcNow.AddHours(-1))try{DeleteOwned(temp);}catch(IOException){}
        }
        lastMaintenance=DateTime.UtcNow;
        return new(removed,removedBytes,ActiveLeases());
    }
    private void Maintain(SqliteConnection c)
    {
        if(DateTime.UtcNow-lastMaintenance>=TimeSpan.FromHours(1))TrimCore(c,false,0,CancellationToken.None);
    }
    public Task<ThumbnailCacheStatistics> GetStatistics(CancellationToken cancellation=default)=>Db().Execute(c=>
    {
        return new ThumbnailCacheStatistics(entries,bytes,Directory.EnumerateFiles(directory).Sum(p=>new FileInfo(p).Length),memoryBytes,memory.Count,hits,misses,stores,evictions,ActiveLeases());
    },cancellation);
    private long IndexBytes()
    {
        long total=0;foreach(string suffix in new[]{"","-wal","-shm"}){string path=Path.Combine(directory,"index.sqlite"+suffix);if(File.Exists(path))total+=new FileInfo(path).Length;}return total;
    }
    private void BoundJournal(SqliteConnection c)
    {
        string wal=Path.Combine(directory,"index.sqlite-wal");if(!File.Exists(wal)||new FileInfo(wal).Length<Math.Min(4L<<20,options.DiskBytes/16))return;
        using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";cmd.ExecuteNonQuery();
    }
    private static void ValidatePng(Stream stream,int edge)
    {
        Span<byte> header=stackalloc byte[24];try{stream.ReadExactly(header);}catch(EndOfStreamException error){throw new InvalidDataException("缓存输入缺少完整的 PNG 文件头。",error);}
        if(!header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||!header.Slice(12,4).SequenceEqual("IHDR"u8))throw new InvalidDataException("缓存输入不是 PNG 缩略图。");
        uint width=BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16,4)),height=BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20,4));if(width<1||height<1||width>edge||height>edge)throw new InvalidDataException("缓存输入超过缩略图像素等级。");
    }
    internal static SafeFileHandle PinDirectory(string path)=>Pin(path,0x80,true);
    internal static SafeFileHandle PinOwnedFile(string path,uint access,uint creation=3,uint share=3)=>Pin(path,access,false,creation,share);
    internal static long OwnedFileLength(string path)
    {
        using var handle=Pin(path,0x80,false,3,7);if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());return checked((long)(((ulong)info.SizeHigh<<32)|info.SizeLow));
    }
    private static SafeFileHandle PinFile(string path,uint access)=>Pin(path,access|0x80,false);
    private static SafeFileHandle Pin(string path,uint access,bool directory,uint creation=3,uint share=3)
    {
        var handle=CreateFileW(path,access,share,IntPtr.Zero,creation,0x00200000|(directory?0x02000000u:0),IntPtr.Zero);
        if(handle.IsInvalid){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}
        if(!GetFileInformationByHandle(handle,out var info)){int code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);}
        if((info.Attributes&1024)!=0||(!directory&&info.NumberOfLinks!=1)||((info.Attributes&16)!=0)!=directory){handle.Dispose();throw new IOException("缓存目录或文件包含重解析点、硬链接或无效对象，已拒绝操作。");}
        return handle;
    }
    internal static FileStream OpenOwnedRead(string path)=>new(PinFile(path,0x80000000),FileAccess.Read,64*1024,false);
    internal static void DeleteOwned(string path)
    {
        using var handle=PinFile(path,0x10000);DeleteOwned(handle);
    }
    internal static void DeleteOwned(SafeFileHandle handle){var disposition=new FileDisposition{Delete=true};if(!SetFileInformationByHandle(handle,4,ref disposition,1))throw new Win32Exception(Marshal.GetLastWin32Error());}
    public async ValueTask DisposeAsync()
    {
        await initialization.WaitAsync().ConfigureAwait(false);
        try{if(disposed)return;disposed=true;WorkerResources.Shared.MemoryPressure-=OnMemoryPressure;if(executor is not null)await executor.DisposeAsync().ConfigureAwait(false);memory.Clear();recent.Clear();memoryBytes=0;ownerLock?.Dispose();ownerLock=null;foreach(var pin in directoryPins)pin.Dispose();directoryPins.Clear();}
        finally{initialization.Release();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileInformation{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,NumberOfLinks,IndexHigh,IndexLow;}
    [StructLayout(LayoutKind.Sequential)]private struct FileDisposition{[MarshalAs(UnmanagedType.U1)]public bool Delete;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation info);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int type,ref FileDisposition info,uint length);
}
