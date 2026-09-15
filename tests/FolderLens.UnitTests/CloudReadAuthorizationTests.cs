using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class CloudReadAuthorizationTests
{
    [Theory]
    [InlineData("image")]
    [InlineData("text")]
    [InlineData("markdown")]
    public async Task ChangedOfflineAttributeNeedsExplicitPerRequestApproval(string kind)
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;Assert.NotNull(project);
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-cloud-authorization",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,kind=="image"?"pixel.png":"document.txt");
        if(kind=="image")await File.WriteAllBytesAsync(source,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a9sAAAAASUVORK5CYII="));
        else await File.WriteAllTextAsync(source,"# Test\nPlain text preview.");
        string component=kind=="image"?"FolderLens.Media.Worker":"FolderLens.Content.Worker";
        await using var worker=new WorkerClient(Path.Combine(project.FullName,"src",component,"bin","Release","net10.0-windows10.0.26100.0","win-x64",component+".exe"),Path.Combine(directory,"work"));
        var stamp=new SourceFileStamp(new FileInfo(source).Length,File.GetLastWriteTimeUtc(source).Ticks);var context=new RequestContext("test",1,1,1,1,1);
        async Task Read(bool allowed)
        {
            if(kind=="image"){var reply=await worker.Request(source,"probe",context,new(),CancellationToken.None,stamp,allowed);await worker.ReleaseAsset(reply);}
            else if(kind=="markdown"){var reply=await worker.RenderMarkdown(source,context,CancellationToken.None,sourceStamp:stamp,allowCloud:allowed);await worker.ReleaseAsset(reply);}
            else await new RemoteTextClient(worker,source,context,sourceStamp:stamp,allowCloud:allowed).ReadWindow(0);
        }
        await Read(false);
        try
        {
            File.SetAttributes(source,File.GetAttributes(source)|FileAttributes.Offline);
            Assert.True((File.GetAttributes(source)&FileAttributes.Offline)!=0);
            await Assert.ThrowsAsync<CloudFileRequiresApprovalException>(()=>Read(false));
            await Read(true);
            await Assert.ThrowsAsync<CloudFileRequiresApprovalException>(()=>Read(false));
        }
        finally{File.SetAttributes(source,FileAttributes.Normal);}
    }
    [Theory]
    [InlineData(0x1000)] [InlineData(0x40000)] [InlineData(0x400000)]
    public void AllRecallAttributesRequireApproval(int value)
    {
        Assert.Throws<IOException>(()=>ApprovedInput.CheckAccess((FileAttributes)value,false));
        ApprovedInput.CheckAccess((FileAttributes)value,true);
        ApprovedInput.CheckAccess(FileAttributes.Archive,false);
    }
}
