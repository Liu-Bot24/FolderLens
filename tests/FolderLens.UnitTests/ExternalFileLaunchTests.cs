using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ExternalFileLaunchTests
{
    [Theory]
    [InlineData("结案.pptx",true)]
    [InlineData("report.PDF",true)]
    [InlineData("letter.docx",true)]
    [InlineData("unknown.exe",false)]
    [InlineData("script.cmd",false)]
    public void KnownDocumentsCanUseExplicitExternalOpen(string name,bool expected)=>Assert.Equal(expected,FolderLens.Core.FileCategories.SupportsExternalOpen(name,"other"));
    [Fact]
    public void CustomPlayerReceivesOneLiteralPathWithoutShellParsing()
    {
        string path=@"C:\素材 空格\视频 & #中文.mp4",player=@"C:\播放器\Player.exe";
        var start=ExternalFileLaunch.CreateStartInfo(path,player);
        Assert.False(start.UseShellExecute);Assert.Equal(player,start.FileName);
        Assert.Equal(path,Assert.Single(start.ArgumentList));Assert.Equal(@"C:\播放器",start.WorkingDirectory);
        Assert.Empty(start.Arguments);
        var associated=ExternalFileLaunch.CreateStartInfo(path,null);
        Assert.True(associated.UseShellExecute);Assert.Equal(path,associated.FileName);Assert.Empty(associated.ArgumentList);
    }

    [Theory]
    [InlineData("https://example.com/movie.mp4")]
    [InlineData(@"relative\movie.mp4")]
    [InlineData(@"C:\movie.mp4:payload")]
    [InlineData("C:\\movie\n.mp4")]
    public void InvalidTargetsNeverBecomeLaunchCommands(string path)=>Assert.ThrowsAny<ArgumentException>(()=>ExternalFileLaunch.CreateStartInfo(path,@"C:\Player.exe"));

    [Fact]
    public async Task ResolvesCurrentIndexedFileAndRejectsChangedMissingOrStaleVersion()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-launch",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"video & 中文.mp4");await File.WriteAllTextAsync(source,"fixture");
        await using var catalog=new CatalogStore(Path.Combine(directory,"index"));await catalog.Initialize();await catalog.SeedBenchmark(1);
        await SetFile(catalog,Path.GetFileName(source),new FileInfo(source).Length,File.GetLastWriteTimeUtc(source).Ticks);
        await using var probe=new SourceFileProbe(Worker());
        var target=await ExternalFileLaunch.Resolve(catalog,probe,directory,"benchmark","000000000001",1,false,CancellationToken.None);
        Assert.Equal(source,target.Path);Assert.Equal("video",target.Kind);
        await Assert.ThrowsAsync<IOException>(()=>ExternalFileLaunch.Resolve(catalog,probe,directory,"benchmark","000000000001",2,false,CancellationToken.None));
        await File.AppendAllTextAsync(source,"changed");
        await Assert.ThrowsAsync<IOException>(()=>ExternalFileLaunch.Resolve(catalog,probe,directory,"benchmark","000000000001",1,false,CancellationToken.None));
        await SetFile(catalog,"missing.mp4",7,1);
        await Assert.ThrowsAsync<FileNotFoundException>(()=>ExternalFileLaunch.Resolve(catalog,probe,directory,"benchmark","000000000001",1,false,CancellationToken.None));
        await SetFile(catalog,@"..\outside.mp4",7,1);
        await Assert.ThrowsAsync<InvalidDataException>(()=>ExternalFileLaunch.Resolve(catalog,probe,directory,"benchmark","000000000001",1,false,CancellationToken.None));
    }
    private static Task<int> SetFile(CatalogStore catalog,string relative,long bytes,long modified)=>catalog.Write(c=>
    {
        using var command=c.CreateCommand();command.CommandText="UPDATE Files SET relative_path=$path,kind='video',logical_bytes=$bytes,mtime_utc_ticks=$time";
        command.Parameters.AddWithValue("$path",relative);command.Parameters.AddWithValue("$bytes",bytes);command.Parameters.AddWithValue("$time",modified);return command.ExecuteNonQuery();
    });
    private static string Worker()
    {
        if(Environment.GetEnvironmentVariable("FOLDERLENS_SCAN_WORKER") is {} value)return value;
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;
        return Path.Combine(root!.FullName,"src","FolderLens.Scan.Worker","bin","Release","net10.0-windows10.0.26100.0","win-x64","FolderLens.Scan.Worker.exe");
    }
}
