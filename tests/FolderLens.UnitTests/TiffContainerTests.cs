using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TiffContainerTests
{
    [Theory]
    [InlineData(4,0,16)]
    [InlineData(8,1,16)]
    [InlineData(8,0,12)]
    public async Task ActualWorkerRejectsInvalidBigTiffHeaders(byte offsetBytes,byte reserved,int length)
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-invalid-tiff",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        byte[] header=new byte[length];header[0]=header[1]=77;header[3]=43;header[5]=offsetBytes;header[7]=reserved;
        string path=Path.Combine(directory,"invalid.tiff");await File.WriteAllBytesAsync(path,header);
        string executable=Path.Combine(project!.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        await using var worker=new WorkerClient(executable,Path.Combine(directory,"worker"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>worker.Request(path,"fit",new RequestContext("root",1,1,1,1,1),new(64,64),CancellationToken.None));
    }
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task ActualWorkerDecodesBothByteOrdersAndOffsetSizes(bool bigTiff,bool bigEndian)
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tiff",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"one-pixel.tiff");
        using(var output=File.Create(path))
        {
            void Number(ulong value,int bytes){byte[] data=new byte[bytes];for(int i=0;i<bytes;i++)data[bigEndian?bytes-1-i:i]=(byte)(value>>(i*8));output.Write(data);}
            output.Write(bigEndian?"MM"u8:"II"u8);Number(bigTiff?43UL:42UL,2);
            if(bigTiff){Number(8,2);Number(0,2);}Number(bigTiff?16UL:8UL,bigTiff?8:4);
            Number(9,bigTiff?8:2);
            void Tag(ushort id,ushort type,ulong value)
            {
                Number(id,2);Number(type,2);Number(1,bigTiff?8:4);
                int length=type==3?2:type==16?8:4;Number(value,length);for(int i=length;i<(bigTiff?8:4);i++)output.WriteByte(0);
            }
            Tag(256,4,1);Tag(257,4,1);Tag(258,3,8);Tag(259,3,1);Tag(262,3,1);
            Tag(273,bigTiff?(ushort)16:(ushort)4,bigTiff?212UL:122UL);Tag(277,3,1);Tag(278,4,1);Tag(279,4,1);
            Number(0,bigTiff?8:4);output.WriteByte(127);
        }
        string executable=Path.Combine(project.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        await using var worker=new WorkerClient(executable,Path.Combine(directory,"worker"));
        var response=await worker.Request(path,"fit",new RequestContext("root",1,1,1,1,1),new(64,64),CancellationToken.None);
        Assert.Equal("fit",response.Message.Quality);Assert.True(File.Exists(response.AssetPath));
    }
}
