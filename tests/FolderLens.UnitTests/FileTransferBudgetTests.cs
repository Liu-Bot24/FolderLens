using FolderLens.Infrastructure;
using Xunit;
namespace FolderLens.UnitTests;
public sealed class FileTransferBudgetTests
{
    [Fact] public void OrdinarySelectionFitsAndHugeSelectionIsRejectedBeforeEnumeration()
    {
        var budget=new FileTransferBudget(10_000);for(int i=0;i<10_000;i++)budget.Add(@"D:\photos\image.jpg");
        Assert.Throws<IOException>(()=>new FileTransferBudget(1_000_000));
    }
    [Fact] public void LongPathsCannotExceedTotalPayloadBudget()
    {
        var budget=new FileTransferBudget(1000);string path=new('a',32000);
        Assert.Throws<IOException>(()=>{for(int i=0;i<1000;i++)budget.Add(path);});
    }
}
