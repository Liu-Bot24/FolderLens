using FolderLens.Infrastructure;
using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class BoundedDiagnosticLogTests
{
    [Fact]
    public async Task DiagnosticFloodStaysBoundedAndFlushesOnShutdown()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using(var log=new BoundedDiagnosticLog(directory))for(int i=0;i<3000;i++)log.Write("fixture",new{sequence=i,value=new string('x',8000)});
        if(!DevelopmentDiagnostics.Enabled){Assert.False(Directory.Exists(directory));return;}
        var files=Directory.GetFiles(directory);Assert.InRange(files.Length,1,2);Assert.True(files.Sum(p=>new FileInfo(p).Length)<1100000);
        foreach(var line in File.ReadLines(Path.Combine(directory,"scan.jsonl")))using(System.Text.Json.JsonDocument.Parse(line)){}
    }
    [Fact]
    public void FatalRecordsFollowBuildPolicyAndStayBounded()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        for(int i=0;i<3;i++)FatalDiagnosticLog.Record(directory,new IOException(new string('x',20000)),"fixture");
        if(!DevelopmentDiagnostics.Enabled){Assert.False(Directory.Exists(directory));return;}
        Assert.Equal(2,Directory.GetFiles(directory).Length);
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"fatal-latest.json")));
        Assert.Equal(16384,document.RootElement.GetProperty("exception").GetString()!.Length);
    }
    [Fact]
    public void BuildIdentityAgreesWithDiagnosticPolicy()
    {
        foreach(var assembly in new[]{typeof(DevelopmentDiagnostics).Assembly,typeof(BoundedDiagnosticLog).Assembly})
        {
            var flavor=assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute),false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(value=>value.Key=="FolderLens.BuildFlavor").Value;
            Assert.Equal(DevelopmentDiagnostics.Enabled?"Development":"Production",flavor);
        }
    }
}
