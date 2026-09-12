using FolderLens.Core;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CapacityTests
{
    [Fact] public void ScopeRowsDoNotDoubleCountAncestors()
    {
        var rows=Capacity.Build([new("root",10,10),new(@"A\a",20,20),new(@"A\AA\a",30,30),new(@"B\b",40,40)]);
        Assert.Equal(100,rows.Single(r=>r.RelativePath=="").SubtreeLogical);Assert.Equal(50,rows.Single(r=>r.RelativePath=="A").SubtreeLogical);
        var level=Capacity.CurrentLevel(rows,"",false);Assert.Equal(100,level.Sum(r=>r.KnownBytes));Assert.Equal(1,level.Sum(r=>r.Fraction));
        var ranking=Capacity.CurrentLevel(rows,"",false,true);Assert.Equal(120,ranking.Sum(r=>r.KnownBytes));Assert.Equal(3,ranking.Count);
    }
    [Fact] public void UnknownAllocationAndEmptyFoldersStayExplicit()
    {
        var rows=Capacity.Build([new("a",0,null)],["empty"]);var level=Capacity.CurrentLevel(rows,"",true);Assert.All(level,r=>Assert.Null(r.Fraction));Assert.Equal(1,level.Single(r=>r.IsDirectFiles).UnknownCount);Assert.Contains(rows,r=>r.RelativePath=="empty"&&r.SubtreeFiles==0);
    }
    [Fact] public void OverflowIsRejected()=>Assert.Throws<OverflowException>(()=>Capacity.Build([new("a",long.MaxValue,0),new("b",1,0)]));
    [Fact] public void EmptyAncestorsAndCrossDirectoryOverflowAreHandled()
    {
        var empty=Capacity.Build([],[@"A\B\C"]);
        Assert.Equal(4,empty.Count);Assert.Contains(Capacity.CurrentLevel(empty,"",false),r=>r.RelativePath=="A");
        Assert.Throws<OverflowException>(()=>Capacity.Build([new(@"A\a",long.MaxValue,0),new(@"B\b",1,0)]));
    }
    [Fact] public void SharesCombineRemainderWithoutOverlappingAncestors()
    {
        var rows=Capacity.Build(Enumerable.Range(1,25).Select(n=>new CapacityFile($@"D{n}\child\file",n,n)));
        var shares=Capacity.Shares(rows,"",false);
        Assert.Equal(21,shares.Count);Assert.Equal(325,shares.Sum(r=>r.KnownBytes));Assert.Equal(1,shares.Sum(r=>r.Fraction)!.Value,10);
        Assert.StartsWith("其他",shares[^1].Label);Assert.Equal(15,shares[^1].KnownBytes);
        Assert.DoesNotContain(shares,r=>r.RelativePath.Contains("child"));
    }
    [Fact] public void AggregationHonorsCancellationForEmptyDirectoryTrees()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>Capacity.Build([],[@"A\B"],cancelled.Token));
    }
}
