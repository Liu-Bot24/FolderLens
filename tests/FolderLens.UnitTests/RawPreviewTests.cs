using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class RawPreviewTests
{
    [Fact]
    public async Task SameRawCanResizeEmbeddedPreviewWithoutReopeningOrDeveloping()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);
        while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);
        string input=Path.Combine(project.FullName,"fixtures","public","sony-a7r4a-14bit.ARW");
        string exe=Path.Combine(project.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        await using var worker=new WorkerClient(exe,Path.Combine(Path.GetTempPath(),"FolderLens-raw-preview",Guid.NewGuid().ToString("N")));
        string? resizedHash=null;
        foreach(int edge in new[]{320,3840,800,4096})
        {
            var reply=await worker.Request(input,"rawEmbedded",new("fixture",1,1,1,1,1),new(edge,edge),CancellationToken.None);
            try{Assert.Equal("rawEmbedded",reply.Message.Quality);Assert.True(new FileInfo(reply.AssetPath!).Length>0);if(edge==4096)resizedHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(reply.AssetPath!)));}
            finally{await worker.ReleaseAsset(reply);}
        }
        // Compare the complete encoded pixels with a freshly opened decoder, not just a successful status.
        var fresh=await worker.Request(input,"rawEmbedded",new("fixture",1,1,2,2,1),new(4096,4096),CancellationToken.None);
        try{Assert.Equal(resizedHash,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fresh.AssetPath!))));}
        finally{await worker.ReleaseAsset(fresh);}
    }
    [Fact]
    public async Task RealWorkerAcceptsEmbeddedBitmapAndJpegAndRejectsTruncation()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);
        while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);
        string exe=Path.Combine(project.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,"raw-preview-check"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try{await process.WaitForExitAsync(timeout.Token);}finally{if(!process.HasExited)process.Kill(true);}
        Assert.True(process.ExitCode==0,await error);
        using var report=System.Text.Json.JsonDocument.Parse(await output);
        Assert.Equal("PASS",report.RootElement.GetProperty("status").GetString());
        Assert.True(report.RootElement.GetProperty("bitmap").GetBoolean());Assert.True(report.RootElement.GetProperty("jpeg").GetBoolean());
        Assert.True(report.RootElement.GetProperty("orientation").GetBoolean());Assert.True(report.RootElement.GetProperty("truncatedRejected").GetBoolean());
    }
    [Fact]
    public async Task PublicArwUsesRealEmbeddedDecoderAndReleasesOutput()
    {
        var project=new DirectoryInfo(AppContext.BaseDirectory);
        while(project is not null&&!File.Exists(Path.Combine(project.FullName,"FolderLens.slnx")))project=project.Parent;
        Assert.NotNull(project);
        string input=Path.Combine(project.FullName,"fixtures","public","sony-a7r4a-14bit.ARW");
        string exe=Path.Combine(project.FullName,"src","FolderLens.Media.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Media.Worker.exe");
        await using var worker=new WorkerClient(exe,Path.Combine(Path.GetTempPath(),"FolderLens-raw-preview",Guid.NewGuid().ToString("N")));
        var calls=new List<string>();var timer=System.Diagnostics.Stopwatch.StartNew();
        var reply=await RawPreview.Open(operation=>{calls.Add(operation);return worker.Request(input,operation,new("fixture",1,1,1,1,1),new(1600,1200),CancellationToken.None);});
        try
        {
            Assert.Equal(new[]{"rawEmbedded"},calls);Assert.Equal("rawEmbedded",reply.Message.Quality);
            Assert.True(new FileInfo(reply.AssetPath!).Length>0);
            var info=reply.Message.Metadata!.Value;Assert.True(info.GetProperty("width").GetInt32()>1000);
            string evidence=Path.Combine(project.FullName,"artifacts","raw-preview-sample.json");
            await File.WriteAllTextAsync(evidence,System.Text.Json.JsonSerializer.Serialize(new{sample="public Sony ARW",elapsedMs=timer.Elapsed.TotalMilliseconds,quality=reply.Message.Quality,operations=calls,nativePresentation="NOT_RUN"}));
        }
        finally{await worker.ReleaseAsset(reply);}
        Assert.False(File.Exists(reply.AssetPath));
    }
    [Fact]
    public async Task EmbeddedPreviewCompletesWithoutDevelopingRaw()
    {
        var calls=new List<string>();
        string result=await RawPreview.Open(operation=>{calls.Add(operation);return Task.FromResult(operation);});
        Assert.Equal("rawEmbedded",result);
        Assert.Equal(new[]{"rawEmbedded"},calls);
    }
    [Fact]
    public async Task MissingEmbeddedPreviewFallsBackOnce()
    {
        var calls=new List<string>();
        string result=await RawPreview.Open(operation=>{calls.Add(operation);return operation=="rawEmbedded"?Task.FromException<string>(new InvalidDataException("DecodeFailed")):Task.FromResult(operation);});
        Assert.Equal("fit",result);Assert.Equal(new[]{"rawEmbedded","fit"},calls);
    }
    [Theory]
    [InlineData("FileChanged")][InlineData("AccessDenied")]
    public async Task InvalidSourceDoesNotStartAnotherDecode(string error)
    {
        int calls=0;
        await Assert.ThrowsAsync<InvalidDataException>(()=>RawPreview.Open<string>(_=>{calls++;throw new InvalidDataException(error);}));
        Assert.Equal(1,calls);
    }
    [Fact]
    public async Task CancellationDoesNotFallBack()
    {
        int calls=0;
        await Assert.ThrowsAsync<OperationCanceledException>(()=>RawPreview.Open<string>(_=>{calls++;throw new OperationCanceledException();}));
        Assert.Equal(1,calls);
    }
}
