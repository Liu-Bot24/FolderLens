using System.Runtime.InteropServices;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ThumbnailCacheTests
{
    [Theory][InlineData(100,128)][InlineData(144,192)][InlineData(192,192)][InlineData(193,256)][InlineData(288,384)][InlineData(577,768)]
    public void ThumbnailTierCoversPhysicalPixelsWithoutUnnecessaryDoubling(double pixels,int expected)
    {
        Assert.Equal(expected,ThumbnailCache.SelectEdge(pixels));
        new ThumbnailCacheKey("tier",1,1,1,expected,"v1").Hash();
        Assert.Throws<ArgumentException>(()=>new ThumbnailCacheKey("tier",1,1,1,160,"v1").Hash());
    }
    [Fact]
    public async Task SevenDayExpiryUsesLastAccessAndCountBudgetEvictsOldest()
    {
        Assert.Equal(TimeSpan.FromDays(7),new ThumbnailCacheOptions().MaxAge);
        Assert.Equal(256L<<20,new ThumbnailCacheOptions().DiskBytes);
        string directory=Temp();Directory.CreateDirectory(directory);string source=Path.Combine(directory,"source.png"),owned=Path.Combine(directory,"owned");await File.WriteAllBytesAsync(source,Png);
        var key=new ThumbnailCacheKey("old",1,1,10,256,"v1");
        await using(var cache=new ThumbnailCache(owned,new(){MinimumFreeBytes=0,DiskEntries=2}))
        {
            await cache.Initialize();using(await cache.Store(key,source)){}using(await cache.Store(key with{EntryId="recent"},source)){}
            await using(var index=new DatabaseExecutor(Path.Combine(owned,"index.sqlite")))await index.Execute(c=>
            {
                using var cmd=c.CreateCommand();cmd.CommandText="UPDATE ThumbnailItems SET created=1,last_access=CASE WHEN cache_key=$old THEN $expired ELSE $recent END";
                cmd.Parameters.AddWithValue("$old",key.Hash());cmd.Parameters.AddWithValue("$expired",DateTime.UtcNow.AddDays(-8).Ticks);cmd.Parameters.AddWithValue("$recent",DateTime.UtcNow.AddDays(-6).Ticks);return cmd.ExecuteNonQuery();
            });
        }
        await using(var reopened=new ThumbnailCache(owned,new(){MinimumFreeBytes=0,DiskEntries=2}))
        {
            await reopened.Initialize();Assert.Null(await reopened.TryGet(key));using(await reopened.TryGet(key with{EntryId="recent"})){}
            using(await reopened.Store(key with{EntryId="second"},source)){}using(await reopened.Store(key with{EntryId="third"},source)){}
            Assert.Null(await reopened.TryGet(key with{EntryId="recent"}));Assert.Equal(2,(await reopened.GetStatistics()).DiskEntries);
        }
    }
    [Fact]
    public async Task BatchedHitsStillDecideEvictionAndSurviveRestart()
    {
        string directory=Temp();Directory.CreateDirectory(directory);string source=Path.Combine(directory,"source.png"),owned=Path.Combine(directory,"owned");await File.WriteAllBytesAsync(source,Png);
        var a=new ThumbnailCacheKey("a",1,1,10,256,"v1");var b=a with{EntryId="b"};var c=a with{EntryId="c"};var d=a with{EntryId="d"};
        var options=new ThumbnailCacheOptions{MinimumFreeBytes=0,DiskEntries=2};
        await using(var cache=new ThumbnailCache(owned,options))
        {
            await cache.Initialize();using(await cache.Store(a,source)){}using(await cache.Store(b,source)){}
            await Task.Delay(20);using(await cache.TryGet(a)){}
            // The pending hit on "a" must be applied before choosing the eviction victim.
            using(await cache.Store(c,source)){}
            Assert.Null(await cache.TryGet(b));using(var kept=await cache.TryGet(a))Assert.NotNull(kept);
            await Task.Delay(20);using(await cache.TryGet(c)){}using(await cache.TryGet(a)){}
        }
        // Hits recorded only in memory are persisted on shutdown: "c" is now the oldest.
        await using(var reopened=new ThumbnailCache(owned,options))
        {
            await reopened.Initialize();using(await reopened.Store(d,source)){}
            Assert.Null(await reopened.TryGet(c));using(var kept=await reopened.TryGet(a))Assert.NotNull(kept);
        }
    }
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
        Assert.NotEqual(key.Hash(),(key with{Representation="rawEmbedded"}).Hash());Assert.NotEqual(key.Hash(),(key with{ProviderVersion="provider-v2"}).Hash());Assert.NotEqual(key.Hash(),(key with{ModifiedUtcTicks=2}).Hash()); Assert.NotEqual((key with{SourceSignature="original-identity"}).Hash(),(key with{SourceSignature="replacement-identity"}).Hash());
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
        // Hit times are written lazily (always before eviction). Persist them before
        // aging every row out-of-band, or the later write would correctly keep entry7.
        await cache.Trim();
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
