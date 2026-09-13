using System.Globalization;

namespace FolderLens.Core;

public static class DateRangeDisplay
{
    public static string Format(DateRange range,TimeZoneInfo? zone=null)
    {
        DateTime Read(string value)=>range.Clock=="captureWall"
            ?DateTime.Parse(value,CultureInfo.InvariantCulture)
            :TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(value,CultureInfo.InvariantCulture),zone??TimeZoneInfo.Local).DateTime;
        var start=Read(range.StartInclusive);var end=Read(range.EndExclusive);
        string label=range.Field=="modified"?"修改日期":range.Field=="created"?"创建日期":"拍摄日期";
        return start.TimeOfDay==TimeSpan.Zero&&end.TimeOfDay==TimeSpan.Zero
            ?$"{label}：{start:yyyy-MM-dd} 至 {end.AddDays(-1):yyyy-MM-dd}"
            :$"{label}：{start:yyyy-MM-dd HH:mm:ss} 至 {end:yyyy-MM-dd HH:mm:ss}（不含结束时刻）";
    }
}
