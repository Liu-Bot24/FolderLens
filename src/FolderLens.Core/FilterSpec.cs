using System.Text.RegularExpressions;

namespace FolderLens.Core;

public sealed record IntRange(long? Min = null, long? Max = null)
{
    public bool Contains(long value) => (Min is null || value >= Min) && (Max is null || value <= Max);
    public void Validate() { if ((Min is null && Max is null) || Min < 0 || Max < 0 || Min > Max) throw new ArgumentException("无效数值区间。"); }
}
public sealed record NumberRange(double? Min = null, double? Max = null)
{
    public bool Contains(double value) => (Min is null || value >= Min) && (Max is null || value <= Max);
    public void Validate() { if ((Min is null && Max is null) || Min < 0 || Max < 0 || Min > Max || (Min is { } min && !double.IsFinite(min)) || (Max is { } max && !double.IsFinite(max))) throw new ArgumentException("无效数值区间。"); }
}
public sealed record SortSpec(string Field = "name", string Direction = "asc", string Nulls = "last", int NaturalKeyVersion = 1);
public sealed record FolderGroupingSpec(bool Enabled=false,string Levels="all",string Field="logicalBytes",string Direction="desc",string CapacityScope="all")
{
    public void Validate()
    {
        if(Levels is not ("all" or "first")||Field is not ("name" or "logicalBytes" or "matchCount")||Direction is not ("asc" or "desc")||CapacityScope is not ("all" or "matches"))
            throw new ArgumentException("文件夹分组条件无效。");
    }
}
public sealed record ExclusionSpec(string RelativePath, string Mode);
public sealed record DateRange(string Field, string Clock, string StartInclusive, string EndExclusive);
public sealed record FilterSpec
{
    public int SchemaVersion { get; init; } = 1;
    public string RootId { get; init; } = "";
    public bool Recursive { get; init; } = true;
    public string DirectoryScope { get; init; } = "";
    public bool ScopeDirectFiles { get; init; }
    public string[] Kinds { get; init; } = ["image"];
    public string[] Formats { get; init; } = [];
    public string Raw { get; init; } = "any";
    public string Animation { get; init; } = "any";
    public bool ShowHidden { get; init; }
    public bool IncludePending { get; init; }
    public string NamePathQuery { get; init; } = "";
    public string SearchScope { get; init; } = "name";
    public Dictionary<string, IntRange> Ranges { get; init; } = [];
    public NumberRange? AspectRatio { get; init; }
    public string Orientation { get; init; } = "any";
    public string[] VideoCodecs { get; init; } = [];
    public string[] AudioCodecs { get; init; } = [];
    public NumberRange? FrameRate { get; init; }
    public DateRange[] Dates { get; init; } = [];
    public ExclusionSpec[] Exclusions { get; init; } = [];
    public DirectoryRule[] DirectoryRules { get; init; } = [];
    public string[] Extensions { get; init; } = [];
    public string[] FileExtensions { get; init; } = [];
    public SortSpec Sort { get; init; } = new();
    public FolderGroupingSpec Grouping { get; init; } = new();

    public bool HasSameScanPolicy(FilterSpec other)=>Recursive==other.Recursive&&
        Exclusions.Where(r=>r.Mode=="skipScan").Select(r=>r.RelativePath.Replace('/','\\')).ToHashSet(StringComparer.Ordinal)
            .SetEquals(other.Exclusions.Where(r=>r.Mode=="skipScan").Select(r=>r.RelativePath.Replace('/','\\')));

    public void Validate()
    {
        Grouping.Validate();
        if(FileExtensions.Length>64||FileExtensions.Distinct(StringComparer.Ordinal).Count()!=FileExtensions.Length||FileExtensions.Any(e=>e.Length>255||e!=e.ToLowerInvariant()||e.Any(c=>char.IsControl(c)||"\\/:*?\"<>|".Contains(c))))throw new ArgumentException("扩展名选择无效。");
        _=new DirectoryRuleSet(DirectoryRules);
        if(Extensions.Length>64||Extensions.Any(e=>!Regex.IsMatch(e,"^[a-z0-9][a-z0-9._+-]{0,31}$",RegexOptions.CultureInvariant)))throw new ArgumentException("扩展名筛选无效。");
        if(DirectoryScope is null||DirectoryScope.Length>32767||DirectoryScope.Any(c=>char.IsControl(c)||":*?\"<>|".Contains(c))||
            DirectoryScope.Length>0&&(Path.IsPathRooted(DirectoryScope)||DirectoryScope.Split('\\','/').Any(p=>p is "" or "." or "..")))
            throw new ArgumentException("浏览目录范围必须是根目录内的相对路径。");
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(RootId) || RootId.Length > 128) throw new ArgumentException("筛选版本或根目录无效。");
        if (Raw is not ("any" or "only" or "exclude") || Animation is not ("any" or "static" or "animated")) throw new ArgumentException("筛选模式无效。");
        if (SearchScope is not ("name" or "nameAndPath") || NamePathQuery.Length > 4096) throw new ArgumentException("搜索条件无效。");
        if (Orientation is not ("any" or "landscape" or "portrait" or "square")) throw new ArgumentException("方向条件无效。");
        if (Kinds.Any(k => k is not ("image" or "video" or "audio" or "text" or "markdown" or "other"))) throw new ArgumentException("未知类别。");
        if (Formats.Any(f => !Regex.IsMatch(f, "^[a-z0-9][a-z0-9._+\\-]{0,31}$", RegexOptions.CultureInvariant))) throw new ArgumentException("格式条件无效。");
        if (Kinds.Distinct(StringComparer.Ordinal).Count() != Kinds.Length || Formats.Distinct(StringComparer.Ordinal).Count() != Formats.Length || VideoCodecs.Concat(AudioCodecs).Any(c => string.IsNullOrEmpty(c) || c.Length > 64) || VideoCodecs.Distinct(StringComparer.Ordinal).Count() != VideoCodecs.Length || AudioCodecs.Distinct(StringComparer.Ordinal).Count() != AudioCodecs.Length) throw new ArgumentException("筛选集合无效。");
        if (Sort.Field is not ("name" or "path" or "logicalBytes" or "allocatedBytes" or "modified" or "created" or "captured" or "width" or "height" or "longEdge" or "shortEdge" or "pixelCount" or "aspectRatio" or "durationMs" or "frameRate" or "format")) throw new ArgumentException("未知排序字段。");
        if (Sort.Direction is not ("asc" or "desc") || Sort.Nulls != "last" || Sort.NaturalKeyVersion != 1) throw new ArgumentException("排序条件无效。");
        foreach (var range in Ranges) { if (range.Key is not ("logicalBytes" or "allocatedBytes" or "width" or "height" or "longEdge" or "shortEdge" or "pixelCount" or "durationMs")) throw new ArgumentException("未知范围字段。"); range.Value.Validate(); }
        AspectRatio?.Validate(); FrameRate?.Validate();
        foreach (var rule in Exclusions) if (rule.Mode is not ("hideView" or "skipScan") || rule.RelativePath.Length > 32767 || rule.RelativePath.Contains(':') || Path.IsPathRooted(rule.RelativePath) || rule.RelativePath.Split('\\', '/').Any(s => s is ".." or "." or "")) throw new ArgumentException("排除路径无效。");
        if (Dates.Length > 3 || Dates.Select(d => d.Field).Distinct(StringComparer.Ordinal).Count() != Dates.Length) throw new ArgumentException("日期字段重复。");
        foreach (var date in Dates) { if (date.Field is not ("modified" or "created" or "captured") || date.Clock is not ("utc" or "captureWall") || (date.Clock == "captureWall" && date.Field != "captured")) throw new ArgumentException("日期字段无效。"); if (DateTicks(date.StartInclusive, date.Clock) >= DateTicks(date.EndExclusive, date.Clock)) throw new ArgumentException("日期范围无效。"); }
    }
    public static long DateTicks(string value, string clock)
    {
        const string iso = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?";
        bool utc = clock == "utc";
        if (clock is not ("utc" or "captureWall") || value is null || value.Length > 40 || !Regex.IsMatch(value, iso + (utc ? @"(?:Z|[+-]\d{2}:\d{2})$" : "$"), RegexOptions.CultureInvariant))
            throw new ArgumentException("日期必须为明确时区的 ISO 时间；拍摄墙钟不可带时区。");
        if (utc && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var instant)) return instant.UtcTicks;
        if (!utc && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var wall)) return wall.Ticks;
        throw new ArgumentException("日期时间无效。");
    }
    public static string[] Words(string query) => Regex.Matches(query, "\"([^\"]+)\"|([^\\s]+)", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
}
