using FolderLens.Core;

namespace FolderLens.Infrastructure;

public sealed record FilterQuery(string StateExpression, string OrderBy, IReadOnlyDictionary<string, object> Parameters)
{
    public string CandidateExpression { get; init; } = "1";
    public string MatchExpression { get; init; } = "1";
}

public static class FilterSql
{
    public static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["name"]="f.name_sort_key",["path"]="f.path_sort_key",["logicalBytes"]="f.logical_bytes",["allocatedBytes"]="f.allocated_bytes",
        ["modified"]="f.mtime_utc_ticks",["created"]="f.ctime_utc_ticks",["captured"]="f.capture_utc_ticks",["width"]="f.display_width",["height"]="f.display_height",
        ["longEdge"]="f.long_edge",["shortEdge"]="f.short_edge",["pixelCount"]="f.pixel_count",["aspectRatio"]="(1.0*f.display_width/f.display_height)",
        ["durationMs"]="f.duration_ms",["frameRate"]="(1.0*f.fps_num/f.fps_den)",["format"]="f.format_id"
    };
    public static FilterQuery Build(FilterSpec filter)
    {
        filter.Validate();
        if (!Columns.TryGetValue(filter.Sort.Field, out string? sort)) throw new ArgumentException("未知排序字段。");
        var parameters = new Dictionary<string, object>();
        var predicates = new List<string>();
        var candidates = new List<string>();
        var missing = new List<(string expression, string group)>();
        string Param(object value) { string key = "$p" + parameters.Count; parameters.Add(key, value); return key; }
        void Known(string expression) { predicates.Add(expression); candidates.Add(expression); }
        void Nullable(string column, string condition, string group)
        {
            predicates.Add(condition);
            missing.Add(($"({column} IS NULL)", group));
        }
        void Set(string column, string[] values, string? group = null)
        {
            if (values.Length == 0) return;
            string condition = $"{column} IN ({string.Join(',', values.Select(v => Param(v)))})";
            if (group is null) Known(condition); else Nullable(column, condition, group);
        }
        string Membership(IEnumerable<string> ids)=>$"((SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=f.directory_id),f.location_key) IN(SELECT directory_location_id,location_key FROM CollectionMembers WHERE collection_id IN({string.Join(',',ids.Select(id=>Param(id))) }))";
        if(filter.CollectionId is {} collection)
        {
            Known(Membership([collection]));
            Known("f.entry_id=(SELECT pick.entry_id FROM Files pick WHERE pick.location_key=f.location_key AND (SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=pick.directory_id)=(SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=f.directory_id) ORDER BY (pick.entry_state='present') DESC,pick.entry_id LIMIT 1)");
        }
        else Known($"f.root_id={Param(filter.RootId)} AND f.entry_state='present'");
        if(filter.CollectionId is null&&filter.ObservedRootEpoch is {} observedEpoch)
            Known($"f.last_seen_scan_id IN (SELECT scan_id FROM ScanRuns WHERE root_id={Param(filter.RootId)} AND root_epoch={Param(observedEpoch)})");
        if(filter.IncludeCollections.Length>0)Known(Membership(filter.IncludeCollections));
        if(filter.ExcludeCollections.Length>0)Known("NOT ("+Membership(filter.ExcludeCollections)+")");
        string directory=filter.DirectoryScope.Replace('/','\\');
        string? directoryPrefix=directory.Length==0?null:Param(directory+"\\");
        if(directoryPrefix is not null)Known($"substr(f.relative_path,1,length({directoryPrefix}))={directoryPrefix}");
        if (!filter.Recursive||filter.ScopeDirectFiles) Known(directoryPrefix is null?"instr(f.relative_path,'\\')=0":$"instr(substr(f.relative_path,length({directoryPrefix})+1),'\\')=0");
        if(filter.CollectionId is null&&filter.MaxFolderLevels is {} levels)
        {
            int scopeDepth=directory.Length==0?0:directory.Count(c=>c=='\\')+1;
            Known($"length(f.relative_path)-length(replace(f.relative_path,'\\',''))<={Param(scopeDepth+levels-1)}");
        }
        if (!filter.ShowHidden) Known("(f.file_attributes & 2)=0");
        Set("f.kind",filter.Kinds);
        Set("lower(ltrim(f.extension,'.'))",filter.Extensions);
        Set("lower(ltrim(f.extension,'.'))",filter.FileExtensions);
        if(filter.DirectoryRules.Any(r=>r.Enabled))
        {
            string rules=Param(System.Text.Json.JsonSerializer.Serialize(filter.DirectoryRules));
            Known(filter.CollectionId is null?$"f.directory_id IN (SELECT directory_id FROM Directories WHERE root_id={Param(filter.RootId)} AND lens_directory_visible(relative_path,{rules}))":$"f.directory_id IN(SELECT d.directory_id FROM Directories d WHERE d.directory_id IN(SELECT source.directory_id FROM CollectionMembers member JOIN Files source ON source.location_key=member.location_key AND (SELECT location_id FROM DirectoryLocationBindings WHERE directory_id=source.directory_id)=member.directory_location_id WHERE member.collection_id={Param(filter.CollectionId)}) AND lens_directory_visible(d.relative_path,{rules}))");
        }
        Set("f.format_id",filter.Formats,"identity");
        if(filter.Raw!="any")
        {
            string extensionRaw=$"lower(f.extension) IN ({string.Join(',',FileKinds.Raw.OrderBy(value=>value).Select(value=>Param(value.ToLowerInvariant())))})";
            Known(filter.Raw=="only"?$"f.kind='image' AND COALESCE(f.is_raw,{extensionRaw})=1":$"f.kind<>'image' OR COALESCE(f.is_raw,{extensionRaw})=0");
        }

        if (filter.Animation != "any") { Known("f.kind='image'"); Nullable("f.is_animated",$"f.is_animated={(filter.Animation == "animated" ? 1 : 0)}","animation"); }
        foreach(var range in filter.Ranges)
        {
            string col = Columns[range.Key], group = range.Key == "allocatedBytes" ? "allocation" : range.Key == "durationMs" ? "media" : "imageGeometry";
            if (range.Key is "width" or "height" or "longEdge" or "shortEdge" or "pixelCount") Known("f.kind IN ('image','video')");
            if (range.Key == "durationMs") Known("f.kind IN ('video','audio')");
            if (range.Value.Min is { } min) Nullable(col,$"{col}>={Param(min)}",group);
            if (range.Value.Max is { } max) Nullable(col,$"{col}<={Param(max)}",group);
        }
        void Geometry(NumberRange? range)
        {
            if (range is null) return;
            Known("f.kind IN ('image','video')");
            if (range.Min is { } min) Nullable(Columns["aspectRatio"],$"{Columns["aspectRatio"]}>={Param(min)}","imageGeometry");
            if (range.Max is { } max) Nullable(Columns["aspectRatio"],$"{Columns["aspectRatio"]}<={Param(max)}","imageGeometry");
        }
        Geometry(filter.AspectRatio);
        if (filter.Orientation != "any")
        {
            Known("f.kind IN ('image','video')");
            string col=Columns["aspectRatio"];
            Nullable(col,filter.Orientation switch { "landscape"=>$"{col}>1.02", "portrait"=>$"{col}<0.98", _=>$"{col} BETWEEN 0.98 AND 1.02" },"imageGeometry");
        }
        if (filter.VideoCodecs.Length>0) {Known("f.kind='video'");Set("f.video_codec",filter.VideoCodecs,"media");}
        if (filter.AudioCodecs.Length>0) {Known("f.kind IN ('audio','video')");Set("f.audio_codec",filter.AudioCodecs,"media");}
        if (filter.FrameRate is { } fps)
        {
            Known("f.kind='video'");
            if(fps.Min is { } min)Nullable(Columns["frameRate"],$"{Columns["frameRate"]}>={Param(min)}","media");
            if(fps.Max is { } max)Nullable(Columns["frameRate"],$"{Columns["frameRate"]}<={Param(max)}","media");
        }
        foreach(var date in filter.Dates)
        {
            string col = date.Clock=="captureWall" ? "f.capture_wall_ticks" : Columns[date.Field];
            Nullable(col,$"{col}>={Param(FilterSpec.DateTicks(date.StartInclusive,date.Clock))} AND {col}<{Param(FilterSpec.DateTicks(date.EndExclusive,date.Clock))}",date.Field == "captured" ? "captureTime" : "");
        }
        // Old saved views may still carry nameAndPath; search now always means filename.
        foreach (string word in FilterSpec.Words(filter.NamePathQuery)) Known($"instr(lens_fold(f.name),{Param(word.ToUpperInvariant())})>0");
        foreach (var rule in filter.Exclusions)
        {
            string dir=rule.RelativePath.Replace('/','\\');
            string exact=Param(dir),prefix=Param(dir+"\\");
            Known($"NOT(f.relative_path={exact} OR substr(f.relative_path,1,length({prefix}))={prefix})");
        }
        string falses=string.Join(" OR ",predicates.Select(p=>$"(({p}) IS FALSE)"));
        string unknown=string.Join(" OR ",predicates.Select(p=>$"(({p}) IS NULL)"));
        var unresolved=missing.Where(m=>m.group.Length>0).ToArray();
        string failed=unresolved.Length==0 ? "0" : string.Join(" OR ",unresolved.Select(m=>$"({m.expression} AND EXISTS(SELECT 1 FROM FieldStates fs WHERE fs.entry_id=f.entry_id AND fs.field_group='{m.group}' AND fs.source_version=f.file_version AND fs.state IN ('failed','unsupported')))"));
        string state=$"CASE WHEN {falses} THEN 'NoMatch' WHEN {unknown} THEN CASE WHEN {failed} THEN 'Unresolvable' ELSE 'Pending' END ELSE 'Match' END";
        // Non-null columns need no null discriminator: it would force SQLite to sort the
        // entire catalog instead of streaming the existing compound index.
        string nullOrder=filter.Sort.Field is "name" or "path" or "logicalBytes" or "modified" ? "" : $"({sort} IS NULL),";
        return new(state,$"{nullOrder}{sort} {filter.Sort.Direction.ToUpperInvariant()},f.path_sort_key,f.entry_id",parameters)
        {
            CandidateExpression=string.Join(" AND ",candidates.Select(p=>$"({p})")),
            MatchExpression=string.Join(" AND ",predicates.Select(p=>$"({p})"))
        };
    }
}
