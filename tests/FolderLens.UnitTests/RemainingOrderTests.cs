using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public class RemainingOrderTests
{
    [Fact]
    public void RanksAgreeWithRemainingListForForwardReverseAndRandomOrders()
    {
        var random=new Random(481);
        foreach(var order in new[]{Enumerable.Range(0,1000).ToArray(),Enumerable.Range(0,1000).Reverse().ToArray(),Enumerable.Range(0,1000).OrderBy(_=>random.Next()).ToArray()})
        {
            var ranks=new RemainingOrder(order.Length);var remaining=Enumerable.Range(0,order.Length).ToList();
            foreach(int item in order){Assert.Equal(remaining.IndexOf(item),ranks.Take(item));remaining.Remove(item);}
        }
    }
    [Fact]
    public void RejectsDuplicateAndOutOfRangeItems()
    {
        var ranks=new RemainingOrder(3);Assert.Equal(1,ranks.Take(1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>ranks.Take(1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>ranks.Take(-1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>ranks.Take(3));
        Assert.Equal(1,ranks.Take(2));Assert.Equal(0,ranks.Take(0));
    }
}
