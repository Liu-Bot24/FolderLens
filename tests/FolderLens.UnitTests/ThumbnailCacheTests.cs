using System.Runtime.InteropServices;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ThumbnailCacheTests
{
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"FolderLens-cache-tests",Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1uoAAAAASUVORK5CYII=");
    [Fact]
    public async Task PersistentHitVersionMissLeaseProtectionAndMemoryLru()
    {
        string directory=Temp();Directory.CreateDirectory(directory);string source=Path.Combine(directory,"source.png");await File.WriteAllBytesAsync(source,Png);
        string owned=Path.Combine(directory,"owned");var key=new ThumbnailCacheKey("fixture",1,1,10,256,"provider-v1");
        var options=new ThumbnailCacheOptions{MemoryBytes=Png.Length,MemoryEntries=1,MinimumFreeBytes=0};
        await using(var cache=new ThumbnailCache(owned,options))
        {
            await cache.Initialize();using(var lease=await cache.Store(key,source)){Assert.True(File.Exists(lease.Path));var cleanup=await cache.Clear();Assert.Equal(0,cleanup.RemovedEntries);Assert.Equal(1,cleanup.ActiveLeases);}
            Assert.Null(await cache.TryGet(key with{FileVersion=2}));
            Assert.Equal(Png,(await cache.GetBytes(key))!.Value.ToArray());
            using(await cache.Store(key with{EntryId="next"},source)){}
            Assert.Equal(Png,(await cache.GetBytes(key with{EntryId="next"}))!.Value.ToArray());
            var stats=await cache.GetStatistics();Assert.Equal(1,stats.MemoryEntries);Assert.Equal(Png.Length,stats.MemoryBytes);Assert.Equal(2,stats.DiskEntries);
        }
        await using(var reopened=new ThumbnailCache(owned,options)){await reopened.Initialize();using(var hit=await reopened.TryGet(key)){Assert.NotNull(hit);using var input=hit.OpenRead();Assert.Equal(Png.Length,input.Length);}var clear=await reopened.Clear();Assert.Equal(2,clear.RemovedEntries);Assert.Equal(0,(await reopened.GetStatistics()).DiskEntries);}
        Assert.Equal(Png,await File.ReadAllBytesAsync(source));
        Assert.NotEqual(key.Hash(),(key with{Representation="rawEmbedded"}).Hash());Assert.NotEqual(key.Hash(),(key with{ProviderVersion="provider-v2"}).Hash());Assert.NotEqual(key.Hash(),(key with{ModifiedUtcTicks=2}).Hash());
    }
    [Fact]
    public async Task RejectsNonOwnedDirectoryAndHardlinkPayloadWithoutTouchingSource()
    {
        string directory=Temp();Directory.CreateDirectory(directory);string source=Path.Combine(directory,"source.png");await File.WriteAllBytesAsync(source,Png);
        await using(var invalid=new ThumbnailCache(directory)){await Assert.ThrowsAsync<IOException>(()=>invalid.Initialize());}
        string owned=Path.Combine(directory,"owned");await using var cache=new ThumbnailCache(owned,new(){MinimumFreeBytes=0});await cache.Initialize();
        var key=new ThumbnailCacheKey("fixture",1,1,10,256,"provider-v1");string cached;
        using(var lease=await cache.Store(key,source))cached=lease.Path;
        // Creating a hardlink outside the owned folder makes payload deletion unsafe;
        // the service must preserve it and the index rather than follow/remove it.
        string alias=Path.Combine(directory,"linked.png");Assert.True(CreateHardLinkW(alias,cached,IntPtr.Zero));
        var result=await cache.Clear();Assert.Equal(0,result.RemovedEntries);Assert.True(File.Exists(alias));Assert.Equal(Png,await File.ReadAllBytesAsync(alias));
    }
    [Fact]
    public async Task DiskLruAndExpiredEntriesStayWithinBudget()
    {
        string directory=Temp();Directory.CreateDirectory(directory);string source=Path.Combine(directory,"source.png");
        // A valid ancillary tEXt chunk makes a small pixel fixture occupy enough bytes
        // to exercise the disk quota without creating a large or slow image dataset.
        byte[] text=new byte[180000];"padding\0"u8.CopyTo(text);Array.Fill(text,(byte)'x',8,text.Length-8);
        using(var output=new BinaryWriter(File.Create(source)))
        {
            output.Write(Png.AsSpan(0,Png.Length-12));Span<byte> size=stackalloc byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size,text.Length);output.Write(size);output.Write("tEXt"u8);output.Write(text);
            uint crc=uint.MaxValue;foreach(byte value in "tEXt"u8.ToArray().Concat(text)){crc^=value;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(size,~crc);output.Write(size);output.Write(Png.AsSpan(Png.Length-12));
        }
        string owned=Path.Combine(directory,"owned");await using var cache=new ThumbnailCache(owned,new(){DiskBytes=1<<20,MinimumFreeBytes=0});await cache.Initialize();
        var key=new ThumbnailCacheKey("entry0",1,1,10,256,"v1");for(int i=0;i<8;i++)using(await cache.Store(key with{EntryId="entry"+i},source)){}
        Assert.Null(await cache.TryGet(key));using(var newest=await cache.TryGet(key with{EntryId="entry7"}))Assert.NotNull(newest);
        Assert.True((await cache.GetStatistics()).TotalDiskBytes<=1<<20);
        await using(var index=new DatabaseExecutor(Path.Combine(owned,"index.sqlite")))await index.Execute(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE ThumbnailItems SET last_access=1";return cmd.ExecuteNonQuery();});
        var trim=await cache.Trim();Assert.True(trim.RemovedEntries>0);Assert.Equal(0,(await cache.GetStatistics()).DiskEntries);
    }
    [Fact]
    public async Task OptionalPersistenceReportsDiskFailureButRejectsInvalidInputAndCancellation()
    {
        string directory=Temp();Directory.CreateDirectory(directory);string source=Path.Combine(directory,"source.png");await File.WriteAllBytesAsync(source,Png);
        await using var cache=new ThumbnailCache(Path.Combine(directory,"owned"),new(){MinimumFreeBytes=long.MaxValue});await cache.Initialize();
        var key=new ThumbnailCacheKey("valid",1,1,10,256,"v1");
        var write=await cache.StoreOptional(key,source);Assert.Null(write.Lease);Assert.Contains("缓存",write.Warning);Assert.Equal(Png,await File.ReadAllBytesAsync(source));
        await File.WriteAllTextAsync(source,"invalid image");
        await Assert.ThrowsAsync<InvalidDataException>(()=>cache.StoreOptional(key,source));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cache.StoreOptional(key,source,new CancellationToken(true)));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
}
