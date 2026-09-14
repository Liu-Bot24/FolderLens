using FolderLens.Infrastructure;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class BoundedDiagnosticLogTests
{
    [Fact]
    public async Task DiagnosticFloodStaysBoundedAndFlushesOnShutdown()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));
        await using(var log=new BoundedDiagnosticLog(directory))for(int i=0;i<3000;i++)log.Write("fixture",new{sequence=i,value=new string('x',8000)});
        var files=Directory.GetFiles(directory);Assert.InRange(files.Length,1,2);Assert.True(files.Sum(p=>new FileInfo(p).Length)<1100000);
        foreach(var line in File.ReadLines(Path.Combine(directory,"scan.jsonl")))using(System.Text.Json.JsonDocument.Parse(line)){}
    }
}
