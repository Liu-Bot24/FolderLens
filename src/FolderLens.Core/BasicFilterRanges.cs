namespace FolderLens.Core;

public static class BasicFilterRanges
{
    public static Dictionary<string,IntRange> MergeExactDimensions(IReadOnlyDictionary<string,IntRange>? advanced,double minMiB,double maxMiB,string? minWidth,string? minHeight)
    {
        var ranges=Merge(advanced,minMiB,maxMiB,double.NaN,double.NaN);
        void Set(string key,string? text)
        {
            long? minimum=null;
            if(!string.IsNullOrWhiteSpace(text))
            {
                if(!long.TryParse(text,System.Globalization.NumberStyles.Integer|System.Globalization.NumberStyles.AllowThousands,System.Globalization.CultureInfo.CurrentCulture,out long value))
                    throw new ArgumentException("宽度和高度必须是有效整数，且不要超过支持的最大值。");
                minimum=value;
            }
            long? maximum=ranges.GetValueOrDefault(key)?.Max;
            ranges.Remove(key);
            if(minimum is null&&maximum is null)return;
            var range=new IntRange(minimum,maximum);range.Validate();ranges[key]=range;
        }
        Set("width",minWidth);Set("height",minHeight);return ranges;
    }
    public static Dictionary<string,IntRange> Merge(IReadOnlyDictionary<string,IntRange>? advanced,double minMiB,double maxMiB,double minWidth,double minHeight)
    {
        var ranges=advanced is null?new Dictionary<string,IntRange>():new Dictionary<string,IntRange>(advanced);
        static long? Value(double value,long scale=1)
        {
            if(double.IsNaN(value))return null;
            double scaled=value*scale;
            if(!double.IsFinite(scaled)||scaled<0||scaled>=9223372036854775808d)throw new ArgumentException("筛选数值超出允许范围。");
            return checked((long)scaled);
        }
        void Set(string key,long? min,long? max)
        {
            ranges.Remove(key);
            if(min is null&&max is null)return;
            var range=new IntRange(min,max);range.Validate();ranges[key]=range;
        }
        Set("logicalBytes",Value(minMiB,1048576),Value(maxMiB,1048576));
        Set("width",Value(minWidth),ranges.GetValueOrDefault("width")?.Max);
        Set("height",Value(minHeight),ranges.GetValueOrDefault("height")?.Max);
        return ranges;
    }
}
