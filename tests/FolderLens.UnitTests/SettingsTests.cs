using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;
public sealed class SettingsTests
{
    [Fact] public async Task AtomicUpdatesRetainConcurrentEntriesAndFailedUpdateLeavesFileIntact()
    {
        var settings=new AtomicSettings(Path.Combine(Path.GetTempPath(),"FolderLens-settings",Guid.NewGuid().ToString("N")));
        await settings.Save("views.json",new Dictionary<string,string>{{"original","old"}});
        await Task.WhenAll(Enumerable.Range(0,20).Select(i=>settings.Update<Dictionary<string,string>>("views.json",latest=>
        {latest??=[];latest[i.ToString()]="saved";return latest;})));
        await settings.Update<Dictionary<string,string>>("views.json",latest=>{latest!["original"]="migrated";return latest;});
        var values=await settings.Load<Dictionary<string,string>>("views.json");Assert.Equal(21,values!.Count);Assert.Equal("migrated",values["original"]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>settings.Update<Dictionary<string,string>>("views.json",_=>throw new InvalidOperationException("conflict")));
        Assert.Equal(values,await settings.Load<Dictionary<string,string>>("views.json"));
    }
    [Fact] public async Task AtomicSettingsKeepPreviousRevisionAndRejectCorruption()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-tests",Guid.NewGuid().ToString("N"));var settings=new AtomicSettings(directory);
        await settings.Save("views.json",new[]{"old"});await settings.Save("views.json",new[]{"new"});Assert.Equal(new[]{"new"},await settings.Load<string[]>("views.json"));Assert.Contains("old",await File.ReadAllTextAsync(Path.Combine(directory,"views.json.bak")));
        await File.WriteAllTextAsync(Path.Combine(directory,"views.json"),"not-json");await Assert.ThrowsAsync<System.Text.Json.JsonException>(()=>settings.Load<string[]>("views.json"));Assert.True(File.Exists(Path.Combine(directory,"views.json.bak")));
    }
    [Fact] public void AllocationFailureIsUnknown()
    {
        var result=FileAllocation.Inspect(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".missing"));Assert.Null(result.Allocated);Assert.Null(result.PhysicalIdentity);
    }
}
