using System.Buffers.Binary;
using System.Text;
using FolderLens.Media.Worker;
using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class BmffImageFormatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWorkerDecodesAvifWithEitherMajorBrand(bool genericMajor)
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-bmff-worker",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        byte[] bytes=await File.ReadAllBytesAsync(Path.Combine(project.FullName,"src","FolderLens.Media.Worker","health","sample.avif"));
        if(genericMajor)"mif1"u8.CopyTo(bytes.AsSpan(8,4));
        string path=Path.Combine(directory,"sample.avif");await File.WriteAllBytesAsync(path,bytes);
        string executable=Path.Combine(project.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        await using var worker=new WorkerClient(executable,Path.Combine(directory,"worker"));
        var reply=await worker.Request(path,"fit",new RequestContext("root",1,1,1,1,1),new(64,64),CancellationToken.None);
        Assert.Equal("avif",reply.Message.Metadata!.Value.GetProperty("format").GetString());Assert.True(File.Exists(reply.AssetPath));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
    }
    private static byte[] Box(string major,string[] compatible,bool extended=false)
    {
        int header=extended?16:8;byte[] data=new byte[header+8+compatible.Length*4];
        BinaryPrimitives.WriteUInt32BigEndian(data,extended?1U:(uint)data.Length);"ftyp"u8.CopyTo(data.AsSpan(4));
        if(extended)BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8),(ulong)data.Length);
        Encoding.ASCII.GetBytes(major).CopyTo(data,header);
        for(int i=0;i<compatible.Length;i++)Encoding.ASCII.GetBytes(compatible[i]).CopyTo(data,header+8+i*4);
        return data;
    }
    [Theory]
    [InlineData("avif","mif1","avif",false)]
    [InlineData("mif1","avif","avif",false)]
    [InlineData("mif1","avif","avif",true)]
    [InlineData("mif1","heic","heic",false)]
    public void IdentifiesMajorAndCompatibleBrands(string major,string compatible,string expected,bool extended)
    {using var stream=new MemoryStream(Box(major,[compatible],extended));Assert.Equal(expected,BmffImageFormat.Read(stream));Assert.Equal(0,stream.Position);}
    [Fact]
    public void FindsCompatibleBrandBeyondOldHeaderBudget()
    {using var stream=new MemoryStream(Box("mif1",Enumerable.Repeat("xxxx",20).Append("avif").ToArray()));Assert.Equal("avif",BmffImageFormat.Read(stream));}
    [Theory]
    [InlineData("avis","avif")]
    [InlineData("isom","mp42")]
    [InlineData("mif1","xxxx")]
    public void RejectsSequenceAndUnrecognizedContainer(string major,string compatible)
    {using var stream=new MemoryStream(Box(major,[compatible]));Assert.Throws<NotSupportedException>(()=>BmffImageFormat.Read(stream));}
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsTruncatedOverflowAndMisalignedBox(int variant)
    {
        byte[] data=Box("avif",["mif1"],true);
        if(variant==0)Array.Resize(ref data,data.Length-1);
        if(variant==1)BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8),ulong.MaxValue);
        if(variant==2)BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8),(ulong)data.Length-1);
        using var stream=new MemoryStream(data);Assert.Throws<InvalidDataException>(()=>BmffImageFormat.Read(stream));
    }
}
