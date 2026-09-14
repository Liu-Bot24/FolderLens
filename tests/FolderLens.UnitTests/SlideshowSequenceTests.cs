using FolderLens.Core;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class SlideshowSequenceTests
{
    [Fact] public async Task FirstPageSkipsVideoAndTextWithoutRequiringSnapshot()
    {
        string[] page=["image","video","text","image"];
        Assert.Equal(3,await SlideshowSequence.Next(page.Length,0,(i,_)=>Task.FromResult(page[i]=="image"),default));
        Assert.Null(await SlideshowSequence.Next(page.Length,3,(i,_)=>Task.FromResult(page[i]=="image"),default));
    }
    [Fact] public async Task RetiredPageReadCannotAdvanceSelection()
    {
        using var stop=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>SlideshowSequence.Next(3,0,(_,_)=>{stop.Cancel();return Task.FromResult(true);},stop.Token));
    }
}
