using System.Globalization;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

// The oracle evaluates domain values directly; it does not execute generated SQL,
// reuse its predicate builder, or compare NaturalOrder.Key to itself.
public sealed class QueryDifferentialTests
{
    private sealed record Row(string Id,string Path,string Kind,bool Present,bool Hidden,Dictionary<string,object?> Fields,Dictionary<string,(string State,long Version)> States);
    private static readonly string[] Sorts=["name","path","logicalBytes","allocatedBytes","modified","created","captured","width","height","longEdge","shortEdge","pixelCount","aspectRatio","durationMs","frameRate","format"];
    private static readonly string[] Ranges=["logicalBytes","allocatedBytes","width","height","longEdge","shortEdge","pixelCount","durationMs"];
    private static string Temp()=>System.IO.Path.Combine(System.IO.Path.GetTempPath(),"FolderLens-query-tests",Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RandomizedFiltersMatchIndependentFourStateOracleAndNaturalOrdering()
    {
        const int seed=732901;
        var rng=new Random(seed);var data=new List<Row>();
        string[] kinds=["image","video","audio","text","markdown","other"];
        long day=new DateTime(2026,9,12).Ticks;
        for(int i=1;i<=180;i++)
        {
            long? w=i%4==0?null:rng.Next(1,2001),h=w is null?null:rng.Next(1,2001);
            string name=$"{new[]{"FILE","file","图像","Été","😀","x ' % _"}[i%6]}{(i%5==0?"00":"")}{i%21}.jpg";
            string path=new[]{"","foo\\","foobar\\","FOO\\","相册\\二级\\"}[i%5]+name;
            var f=new Dictionary<string,object?>
            {
                ["name"]=name,["path"]=path,["logicalBytes"]=(long)rng.Next(0,20001),["allocatedBytes"]=i%3==0?null:(long)rng.Next(0,24001),
                ["width"]=w,["height"]=h,["longEdge"]=w is null?null:Math.Max(w.Value,h!.Value),["shortEdge"]=w is null?null:Math.Min(w.Value,h!.Value),["pixelCount"]=w*h,
                ["aspectRatio"]=w is null?null:(double)w/h!,["durationMs"]=i%4==1?null:(long)rng.Next(0,50001),["frameRate"]=i%4==2?null:rng.Next(0,121)/2.0,
                ["modified"]=day+(long)rng.Next(-3,4)*TimeSpan.TicksPerDay,["created"]=i%4==3?null:day+(long)rng.Next(-3,4)*TimeSpan.TicksPerDay,
                ["captured"]=i%3==1?null:day+(long)rng.Next(-3,4)*TimeSpan.TicksPerDay,["captureWall"]=i%4==2?null:day+(long)rng.Next(-3,4)*TimeSpan.TicksPerDay,
                ["raw"]=i%3==0?null:i%3==1,["animation"]=i%3==1?null:i%3==2,["format"]=i%4==0?null:new[]{"jpeg","arw","png"}[i%3],
                ["videoCodec"]=i%3==0?null:new[]{"h264","hevc"}[i%2],["audioCodec"]=i%3==1?null:new[]{"aac","opus"}[i%2]
            };
            var states=new Dictionary<string,(string,long)>();
            foreach(string g in new[]{"identity","animation","imageGeometry","captureTime","allocation","media"})
                states[g]=(new[]{"failed","unsupported","deferredOffline","pending","notRequested","ready"}[rng.Next(6)],rng.Next(4)==0?2L:1L);
            data.Add(new(i.ToString("D12"),path,kinds[i%6],i%17!=0,i%7==0,f,states));
        }
        await using var catalog=new CatalogStore(Temp());await catalog.Initialize();await catalog.SeedBenchmark(data.Count);
        await catalog.Write(c=>
        {
            c.CreateFunction("lens_fold",(string s)=>s.ToUpperInvariant(),true);
            using var t=c.BeginTransaction();
            foreach(var row in data)
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="""
                    UPDATE Files SET name=$name,relative_path=$path,canonical_key=$id||$path,name_sort_key=$namekey,path_sort_key=$pathkey,kind=$kind,
                    entry_state=$present,file_attributes=$hidden,logical_bytes=$bytes,allocated_bytes=$allocated,display_width=$w,display_height=$h,
                    long_edge=$long,short_edge=$short,pixel_count=$pixels,duration_ms=$duration,fps_num=$fps,fps_den=2,mtime_utc_ticks=$modified,
                    ctime_utc_ticks=$created,capture_utc_ticks=$captured,capture_wall_ticks=$wall,capture_offset_minutes=0,is_raw=$raw,is_animated=$animated,
                    format_id=$format,video_codec=$vc,audio_codec=$ac WHERE entry_id=$id;
                    """;
                void P(string key,object? value)=>cmd.Parameters.AddWithValue(key,value??DBNull.Value);
                P("$id",row.Id);P("$name",row.Fields["name"]);P("$path",row.Path);P("$namekey",NaturalOrder.Key((string)row.Fields["name"]!));P("$pathkey",NaturalOrder.Key(row.Path));P("$kind",row.Kind);P("$present",row.Present?"present":"missing");P("$hidden",row.Hidden?2:0);
                foreach(var pair in new[]{("$bytes","logicalBytes"),("$allocated","allocatedBytes"),("$w","width"),("$h","height"),("$long","longEdge"),("$short","shortEdge"),("$pixels","pixelCount"),("$duration","durationMs"),("$modified","modified"),("$created","created"),("$captured","captured"),("$raw","raw"),("$animated","animation"),("$format","format"),("$vc","videoCodec"),("$ac","audioCodec")})P(pair.Item1,row.Fields[pair.Item2]);
                // UTC data requires a wall-clock in the schema. Keep the independently
                // selected wall clock when supplied, otherwise use the UTC instant.
                row.Fields["captureWall"]??=row.Fields["captured"];P("$wall",row.Fields["captureWall"]);
                P("$fps",row.Fields["frameRate"] is double fps?(long)(fps*2):null);cmd.ExecuteNonQuery();
                foreach(var state in row.States){using var fs=c.CreateCommand();fs.Transaction=t;fs.CommandText="INSERT INTO FieldStates(entry_id,field_group,source_version,state) VALUES($id,$g,$v,$s)";fs.Parameters.AddWithValue("$id",row.Id);fs.Parameters.AddWithValue("$g",state.Key);fs.Parameters.AddWithValue("$v",state.Value.Version);fs.Parameters.AddWithValue("$s",state.Value.State);fs.ExecuteNonQuery();}
            }
            t.Commit();return 0;
        });
        for(int sample=0;sample<220;sample++)
        {
            var f=new FilterSpec
            {
                RootId="benchmark",Kinds=sample%4==0?[]:sample%4==1?["image","video"]:[kinds[rng.Next(6)]],Recursive=sample%7!=0,ShowHidden=sample%3==0,
                Formats=sample%5==0?["jpeg","png"]:[],Raw=new[]{"any","only","exclude"}[rng.Next(3)],Animation=new[]{"any","static","animated"}[rng.Next(3)],
                Sort=new(Sorts[sample%Sorts.Length],sample%2==0?"asc":"desc"),IncludePending=sample%3==0,
                Orientation=sample%6==0?new[]{"landscape","portrait","square"}[rng.Next(3)]:"any",
                AspectRatio=sample%9==0?new(.5,2):null,FrameRate=sample%8==0?new(12,60):null,
                VideoCodecs=sample%10==0?["hevc"]:[],AudioCodecs=sample%11==0?["aac","opus"]:[],
                NamePathQuery=sample%5==0?"\"x ' % _\"":sample%7==0?"相册 jpg":"",SearchScope=sample%2==0?"name":"nameAndPath",
                Exclusions=sample%4==0?[new("foo",sample%2==0?"hideView":"skipScan")]:[],
                Dates=sample%5==0?[new(sample%3==0?"captured":sample%3==1?"created":"modified","utc","2026-09-11T00:00:00Z","2026-09-14T00:00:00Z")]:sample%13==0?[new("captured","captureWall","2026-09-11T00:00:00","2026-09-14T00:00:00")]:[]
            };
            if(sample%2==0)f.Ranges[Ranges[sample%Ranges.Length]]=new(sample%4==0?0:100,sample%4==0?null:5000);
            if(sample%17==0)f.Ranges["logicalBytes"]=new(19000,null);
            var actual=await catalog.Read(c=>
            {
                c.CreateFunction("lens_fold",(string s)=>s.ToUpperInvariant(),true);var q=FilterSql.Build(f);using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT f.entry_id, {q.StateExpression} FROM Files f ORDER BY {q.OrderBy}";foreach(var p in q.Parameters)cmd.Parameters.AddWithValue(p.Key,p.Value);using var r=cmd.ExecuteReader();var result=new List<(string,string)>();while(r.Read())result.Add((r.GetString(0),r.GetString(1)));return result;
            });
            var sorted=data.OrderBy(x=>x,Comparer<Row>.Create((a,b)=>CompareRow(a,b,f.Sort))).ToArray();
            Assert.True(actual.SequenceEqual(sorted.Select(row=>(row.Id,Evaluate(row,f)))), $"Oracle mismatch seed={seed}, sample={sample}, filter={System.Text.Json.JsonSerializer.Serialize(f)}");
            if(sample%17==0)
            {
                var first=await catalog.ReadFirstPage(f);string state=f.IncludePending?"Pending":"Match";
                Assert.Equal(sorted.Where(x=>Evaluate(x,f)==state).Take(256).Select(x=>x.Id),first.Items.Select(x=>x.EntryId));
            }
        }
    }

    private static string Evaluate(Row r,FilterSpec f)
    {
        var states=new List<string>();
        void Known(bool ok)=>states.Add(ok?"Match":"NoMatch");
        void Test(string key,Func<object,bool> match,string group)
        {
            if(r.Fields[key] is {} value){Known(match(value));return;}
            states.Add(r.States.TryGetValue(group,out var status)&&status.Version==1&&status.State is "failed" or "unsupported"?"Unresolvable":"Pending");
        }
        Known(r.Present);Known(f.Recursive||!r.Path.Contains('\\'));Known(f.ShowHidden||!r.Hidden);Known(f.Kinds.Length==0||f.Kinds.Contains(r.Kind));
        if(f.Formats.Length>0)Test("format",v=>f.Formats.Contains((string)v),"identity");
        bool raw=r.Fields["raw"] is bool confirmed?confirmed:FileKinds.Raw.Contains(System.IO.Path.GetExtension(r.Path));
        if(f.Raw=="only")Known(r.Kind=="image"&&raw);
        if(f.Raw=="exclude"&&r.Kind=="image")Known(!raw);
        if(f.Animation!="any"){Known(r.Kind=="image");Test("animation",v=>(bool)v==(f.Animation=="animated"),"animation");}
        bool GeometryApplicable()=>r.Kind is "image" or "video";
        foreach(var range in f.Ranges)
        {
            string group=range.Key=="allocatedBytes"?"allocation":range.Key=="durationMs"?"media":"imageGeometry";
            if(range.Key is "width" or "height" or "longEdge" or "shortEdge" or "pixelCount")Known(GeometryApplicable());
            if(range.Key=="durationMs")Known(r.Kind is "video" or "audio");
            Test(range.Key,v=>(range.Value.Min is null||(long)v>=range.Value.Min)&&(range.Value.Max is null||(long)v<=range.Value.Max),group);
        }
        if(f.AspectRatio is {} aspect){Known(GeometryApplicable());Test("aspectRatio",v=>(aspect.Min is null||(double)v>=aspect.Min)&&(aspect.Max is null||(double)v<=aspect.Max),"imageGeometry");}
        if(f.Orientation!="any"){Known(GeometryApplicable());Test("aspectRatio",v=>f.Orientation=="landscape"?(double)v>1.02:f.Orientation=="portrait"?(double)v<.98:(double)v>=.98&&(double)v<=1.02,"imageGeometry");}
        if(f.VideoCodecs.Length>0){Known(r.Kind=="video");Test("videoCodec",v=>f.VideoCodecs.Contains((string)v),"media");}
        if(f.AudioCodecs.Length>0){Known(r.Kind is "video" or "audio");Test("audioCodec",v=>f.AudioCodecs.Contains((string)v),"media");}
        if(f.FrameRate is {} fps){Known(r.Kind=="video");Test("frameRate",v=>(fps.Min is null||(double)v>=fps.Min)&&(fps.Max is null||(double)v<=fps.Max),"media");}
        foreach(var date in f.Dates)
        {
            long lo=date.Clock=="utc"?DateTimeOffset.Parse(date.StartInclusive,CultureInfo.InvariantCulture).UtcTicks:DateTime.Parse(date.StartInclusive,CultureInfo.InvariantCulture).Ticks;
            long hi=date.Clock=="utc"?DateTimeOffset.Parse(date.EndExclusive,CultureInfo.InvariantCulture).UtcTicks:DateTime.Parse(date.EndExclusive,CultureInfo.InvariantCulture).Ticks;
            Test(date.Clock=="captureWall"?"captureWall":date.Field,v=>(long)v>=lo&&(long)v<hi,date.Field=="captured"?"captureTime":"");
        }
        foreach(var term in Words(f.NamePathQuery))Known(((string)r.Fields["name"]!).ToUpperInvariant().Contains(term.ToUpperInvariant(),StringComparison.Ordinal));
        foreach(var exclusion in f.Exclusions){string p=exclusion.RelativePath.Replace('/','\\');Known(r.Path!=p&&!r.Path.StartsWith(p+"\\",StringComparison.Ordinal));}
        return states.Contains("NoMatch")?"NoMatch":states.Contains("Unresolvable")?"Unresolvable":states.Contains("Pending")?"Pending":"Match";
    }
    private static IEnumerable<string> Words(string query)
    {
        for(int i=0;i<query.Length;)
        {
            if(char.IsWhiteSpace(query[i])){i++;continue;}
            int end;if(query[i]=='"'&&(end=query.IndexOf('"',i+1))>i+1){yield return query[(i+1)..end];i=end+1;}
            else{int start=i++;while(i<query.Length&&!char.IsWhiteSpace(query[i]))i++;yield return query[start..i];}
        }
    }
    private static int CompareRow(Row a,Row b,SortSpec sort)
    {
        object? x=a.Fields[sort.Field],y=b.Fields[sort.Field];int result;
        if(x is null||y is null)result=x is null?(y is null?0:1):-1;
        else{result=sort.Field is "name" or "path"?NaturalCompare((string)x,(string)y):x is string text?string.CompareOrdinal(text,(string)y):Convert.ToDouble(x,CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(y,CultureInfo.InvariantCulture));if(sort.Direction=="desc")result=-result;}
        if(result!=0)return result;result=NaturalCompare(a.Path,b.Path);return result!=0?result:string.CompareOrdinal(a.Id,b.Id);
    }
    private static int NaturalCompare(string a,string b)
    {
        int ai=0,bi=0;
        while(ai<a.Length&&bi<b.Length)
        {
            bool ad=a[ai] is >= '0' and <= '9',bd=b[bi] is >= '0' and <= '9';
            if(ad!=bd)return ad?-1:1;
            if(ad)
            {
                int ae=ai+1,be=bi+1;while(ae<a.Length&&a[ae] is >= '0' and <= '9')ae++;while(be<b.Length&&b[be] is >= '0' and <= '9')be++;
                string an=a[ai..ae].TrimStart('0'),bn=b[bi..be].TrimStart('0');int n=an.Length.CompareTo(bn.Length);if(n==0)n=string.CompareOrdinal(an,bn);if(n!=0)return n;ai=ae;bi=be;
            }
            else{int n=char.ToUpperInvariant(a[ai++]).CompareTo(char.ToUpperInvariant(b[bi++]));if(n!=0)return n;}
        }
        int tail=(a.Length-ai).CompareTo(b.Length-bi);return tail!=0?tail:string.CompareOrdinal(a,b);
    }
    [Fact]
    public void StrictDatesRejectMachineTimezoneAndRepeatedFields()
    {
        var filter=new FilterSpec{RootId="r"};
        Assert.Throws<ArgumentException>(()=>(filter with{Dates=[new("created","utc","2026-09-11T00:00:00","2026-09-12T00:00:00")]}).Validate());
        Assert.Throws<ArgumentException>(()=>(filter with{Dates=[new("captured","captureWall","2026-09-11T00:00:00Z","2026-09-12T00:00:00Z")]}).Validate());
        Assert.Throws<ArgumentException>(()=>(filter with{Dates=[new("created","utc","2026-09-11T00:00:00Z","2026-09-12T00:00:00Z"),new("created","utc","2026-09-11T00:00:00Z","2026-09-12T00:00:00Z")]}).Validate());
        Assert.Equal(new DateTime(2026,9,11,16,0,0).Ticks,FilterSpec.DateTicks("2026-09-12T00:00:00+08:00","utc"));
        Assert.Equal(new DateTime(2026,9,12).Ticks,FilterSpec.DateTicks("2026-09-12T00:00:00","captureWall"));
    }
}
