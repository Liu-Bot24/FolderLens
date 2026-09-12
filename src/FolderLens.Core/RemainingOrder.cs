namespace FolderLens.Core;

/// <summary>Ranks items left in their original order as each is moved to a completed prefix.</summary>
public sealed class RemainingOrder
{
    private readonly int[] tree;
    private readonly bool[] taken;
    public RemainingOrder(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        tree=new int[count+1];taken=new bool[count];
        for(int i=1;i<tree.Length;i++)tree[i]=i&-i;
    }
    public int Take(int originalIndex)
    {
        if((uint)originalIndex>=(uint)taken.Length||taken[originalIndex])throw new ArgumentOutOfRangeException(nameof(originalIndex));
        int rank=0;
        for(int i=originalIndex;i>0;i-=i&-i)rank+=tree[i];
        for(int i=originalIndex+1;i<tree.Length;i+=i&-i)tree[i]--;
        taken[originalIndex]=true;
        return rank;
    }
}
