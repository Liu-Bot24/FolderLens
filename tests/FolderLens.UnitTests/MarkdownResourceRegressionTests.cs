using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MarkdownResourceRegressionTests
{
    [Fact]
    public void AuthorizedResourceCannotChangeToAnOutsideObjectBeforeRead()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-markdown-authorization",Guid.NewGuid().ToString("N"));
        string root=Path.Combine(fixture,"root"),inside=Path.Combine(root,"media"),moved=Path.Combine(root,"retired"),outside=Path.Combine(fixture,"outside");
        Directory.CreateDirectory(inside);Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(inside,"image.png"),"inside!");File.WriteAllText(Path.Combine(outside,"image.png"),"outside");
        using(var authorized=LocalResourceRules.AuthorizeImage(root,Path.Combine(root,"note.md"),"media/image.png"))
        {
            Assert.Throws<IOException>(()=>Directory.Move(inside,moved));
            Assert.Equal("inside!",File.ReadAllText(authorized.Path));
            Assert.Equal(FolderLens.Contracts.FileReadObservation.Read(authorized.Path).Signature,authorized.Stamp.SourceSignature);
        }
        Directory.Move(inside,moved);
        var start=new System.Diagnostics.ProcessStartInfo("pwsh.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '"+inside.Replace("'","''")+"' -Target '"+outside.Replace("'","''")+"' -ErrorAction Stop | Out-Null");
        using var process=System.Diagnostics.Process.Start(start)!;
        if(!process.WaitForExit(10000)){process.Kill(entireProcessTree:true);throw new TimeoutException("Fixture junction creation timed out.");}
        Assert.Equal(0,process.ExitCode);
        Assert.Throws<UnauthorizedAccessException>(()=>LocalResourceRules.AuthorizeImage(root,Path.Combine(root,"note.md"),"media/image.png"));
    }
    [Fact]
    public async Task RemoteAuthorizationSurvivesProbeRetirementUntilConsumptionEnds()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"FolderLens-markdown-transfer",Guid.NewGuid().ToString("N")),directory=Path.Combine(fixture,"media");
        Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"image.png"),"generated");
        var probe=new SourceFileProbe();SourceImageLease? authorized=null;
        try
        {
            authorized=await probe.AuthorizeImage(fixture,Path.Combine(fixture,"note.md"),"media/image.png",CancellationToken.None);
            await probe.DisposeAsync();
            Assert.Throws<IOException>(()=>Directory.Move(directory,directory+"-moved"));
            Assert.Equal("generated",File.ReadAllText(authorized.Path));
            Assert.Equal(FolderLens.Contracts.FileReadObservation.Read(authorized.Path).Signature,authorized.Stamp.SourceSignature);
            authorized.Dispose();authorized=null;
            Directory.Move(directory,directory+"-moved");Assert.True(Directory.Exists(directory+"-moved"));
        }
        finally{authorized?.Dispose();await probe.DisposeAsync();}
    }
    [Fact]
    public void OfflineEmbeddedImageIsDeniedBeforeOpeningContent()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string image=Path.Combine(root,"online.png");File.WriteAllText(image,"fixture");File.SetAttributes(image,FileAttributes.Offline);
        Assert.True(FileAllocation.IsDeferred((long)File.GetAttributes(image)));
        using var locked=File.Open(image,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        Assert.Throws<UnauthorizedAccessException>(()=>LocalResourceRules.ResolveImage(root,Path.Combine(root,"note.md"),"online.png"));
    }
    [Fact]
    public void LocalEmbeddedImageWithinRealRootIsAllowed()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"image.png"),"fixture");
        Assert.EndsWith("image.png",LocalResourceRules.ResolveImage(root,Path.Combine(root,"note.md"),"image.png"));
        Assert.Throws<UnauthorizedAccessException>(()=>LocalResourceRules.ResolveImage(root,Path.Combine(root,"note.md"),"../image.png"));
    }
}
