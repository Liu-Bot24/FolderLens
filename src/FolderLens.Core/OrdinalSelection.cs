namespace FolderLens.Core;

public readonly record struct OrdinalRange(long Start,long Count)
{
    public long End=>checked(Start+Count);
}

public static class OrdinalSelection
{
    public static IReadOnlyList<OrdinalRange> Normalize(IEnumerable<OrdinalRange> ranges,long total)
    {
        if(total<0)throw new ArgumentOutOfRangeException(nameof(total));
        var ordered=ranges.ToArray();
        foreach(var range in ordered)if(range.Start<0||range.Count<=0||range.Start>total||range.Count>total-range.Start)throw new ArgumentException("所选范围超出当前结果。");
        Array.Sort(ordered,(left,right)=>left.Start.CompareTo(right.Start));var merged=new List<OrdinalRange>();
        foreach(var range in ordered)
        {
            if(merged.Count==0||merged[^1].End<range.Start)merged.Add(range);
            else{var previous=merged[^1];merged[^1]=new(previous.Start,Math.Max(previous.End,range.End)-previous.Start);}
        }
        return merged;
    }
}
