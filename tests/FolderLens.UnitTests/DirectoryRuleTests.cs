using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DirectoryRuleTests
{
    [Theory]
    [InlineData("cache*","cache",false)]
    [InlineData("cache*","CACHE01",false)]
    [InlineData("cache*","mycache",true)]
    [InlineData("cache?","cache1",false)]
    [InlineData("cache?","cache",true)]
    [InlineData("cache?","cache12",true)]
    [InlineData("[cache]*","cache1",true)]
    public void WildcardsMatchWholeNames(string pattern,string path,bool visible)
        =>Assert.Equal(visible,new DirectoryRuleSet([new("exclude","name","wildcard",pattern)]).IsVisible(path));

    [Fact]
    public void WildcardsRespectSegmentsChildrenAndLiteralModes()
    {
        var rule=new DirectoryRule("exclude","path","wildcard","A/cache?",IncludeChildren:false);
        var set=new DirectoryRuleSet([rule]);
        Assert.False(set.IsVisible(@"a\cache1"));Assert.True(set.IsVisible(@"a\cache1\child"));
        Assert.True(set.IsVisible(@"a\nested\cache1"));Assert.True(set.IsVisible(@"a\cache12"));
        Assert.False(new DirectoryRuleSet([rule with{IncludeChildren=true}]).IsVisible(@"a\cache1\child"));
        Assert.True(new DirectoryRuleSet([new("exclude","path","wildcard","A/*/cache",false)]).IsVisible(@"A\one\two\cache"));
        Assert.True(new DirectoryRuleSet([rule with{Enabled=false}]).IsVisible(@"a\cache1"));
        Assert.True(new DirectoryRuleSet([new("exclude","name","contains","cache*")]).IsVisible("cache1"));
        Assert.Throws<ArgumentException>(()=>new DirectoryRuleSet([rule with{Pattern="../cache*"}]));
    }
    [Fact]
    public void SqlEvaluatesFolderRulesPerDirectoryInsteadOfPerFile()
    {
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");connection.Open();
        using(var setup=connection.CreateCommand())
        {
            setup.CommandText="""
                CREATE TABLE Directories(directory_id TEXT,root_id TEXT,relative_path TEXT);
                CREATE TABLE Files(directory_id TEXT,root_id TEXT,entry_state TEXT,file_attributes INTEGER,kind TEXT);
                INSERT INTO Directories VALUES('d','r','images');
                WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<10000)
                INSERT INTO Files SELECT 'd','r','present',0,'image' FROM n;
                """;setup.ExecuteNonQuery();
        }
        int calls=0;connection.CreateFunction("lens_directory_visible",(string path,string rules)=>{calls++;return true;});
        var query=FilterSql.Build(new FilterSpec{RootId="r",DirectoryRules=[new("exclude","name","equals","cache")]});
        using var command=connection.CreateCommand();command.CommandText="SELECT count(*) FROM Files f WHERE "+query.MatchExpression;
        foreach(var entry in query.Parameters)command.Parameters.AddWithValue(entry.Key,entry.Value);
        Assert.Equal(10000L,command.ExecuteScalar());Assert.Equal(1,calls);
    }
    [Theory]
    [InlineData("图片\\仅预览",false)]
    [InlineData("视频\\仅预览\\小图",false)]
    [InlineData("视频\\预览",true)]
    [InlineData("图片\\仅预览副本",true)]
    [InlineData("",true)]
    public void NamesMatchCompleteSegments(string path,bool visible)=>Assert.Equal(visible,new DirectoryRuleSet([new("exclude","name","equals","仅预览")]).IsVisible(path));

    [Fact]
    public void IncludeExcludeAndKeepHaveStablePriorityAndCanBePaused()
    {
        DirectoryRule[] rules=[new("include","path","equals","A"),new("exclude","name","equals","预览"),new("keep","path","equals",@"A\视频\预览")];
        var set=new DirectoryRuleSet(rules);
        Assert.True(set.IsVisible(@"A\图片"));Assert.False(set.IsVisible(@"B\图片"));
        Assert.False(set.IsVisible(@"A\图片\预览"));Assert.True(set.IsVisible(@"a\视频\预览\封面"));
        Assert.False(set.IsVisible(@"A\视频\预览副本\预览"));
        Assert.True(new DirectoryRuleSet([rules[1] with{Enabled=false}]).IsVisible("预览"));
        Assert.True(new DirectoryRuleSet([rules[1] with{IncludeChildren=false}]).IsVisible(@"预览\子目录"));
        Assert.False(new DirectoryRuleSet([rules[1] with{IncludeChildren=false}]).IsVisible("预览"));
        Assert.True(new FilterSpec{RootId="root"}.HasSameScanPolicy(new FilterSpec{RootId="root",DirectoryRules=rules}));
    }
    [Fact]
    public void RegexAndSimpleModesValidateAndMatch()
    {
        var set=new DirectoryRuleSet([new("exclude","name","regex","^(仅预览|缓存)$")]);
        Assert.False(set.IsVisible(@"A\缓存"));Assert.True(set.IsVisible(@"A\缓存备份"));
        Assert.False(new DirectoryRuleSet([new("exclude","name","contains","预览")]).IsVisible("仅预览"));
        Assert.True(new DirectoryRuleSet([new("exclude","name","startsWith","预览")]).IsVisible("仅预览"));
        Assert.Throws<ArgumentException>(()=>new DirectoryRuleSet([new("exclude","name","regex","[")]));
        Assert.Throws<ArgumentException>(()=>new DirectoryRuleSet([new("exclude","name","regex","(?=a)")]));
        Assert.Throws<ArgumentException>(()=>new DirectoryRuleSet([new("exclude","path","equals","../outside")]));
        Assert.Throws<ArgumentException>(()=>new DirectoryRuleSet(Enumerable.Repeat(new DirectoryRule("exclude","name","equals","a"),65)));
    }

    [Fact]
    public async Task DirectoryRulesAgreeAcrossFirstPageSnapshotGroupingAndPreview()
    {
        await using var catalog=new CatalogStore(Path.Combine(Path.GetTempPath(),"FolderLens-rule",Guid.NewGuid().ToString("N")));
        await catalog.Initialize();await catalog.SeedBenchmark(5);
        string[] paths=["图片",@"图片\仅预览","视频",@"视频\预览",@"视频\仅预览"];
        await catalog.Write(c=>
        {
            for(int i=0;i<paths.Length;i++)
            {
                using var cmd=c.CreateCommand();cmd.CommandText="""
                    INSERT INTO Directories(directory_id,root_id,parent_id,name,relative_path,canonical_key,case_mode) VALUES($dir,'benchmark',$parent,$name,$path,$path,'sensitive');
                    UPDATE Files SET directory_id=$dir,relative_path=$file WHERE entry_id=$entry;
                    """;string parent=i==1?"d0":i>=3?"d2":"benchmark-dir";
                cmd.Parameters.AddWithValue("$dir","d"+i);cmd.Parameters.AddWithValue("$parent",parent);cmd.Parameters.AddWithValue("$name",Path.GetFileName(paths[i]));cmd.Parameters.AddWithValue("$path",paths[i]);
                cmd.Parameters.AddWithValue("$file",paths[i]+"\\仅预览.jpg");cmd.Parameters.AddWithValue("$entry",(i+1).ToString("D12"));cmd.ExecuteNonQuery();
            }
            return true;
        });
        var filter=new FilterSpec{RootId="benchmark",DirectoryRules=[new("exclude","name","wildcard","仅预?")]};
        Assert.Equal(3,(await catalog.ReadFirstPage(filter)).Items.Count);
        Assert.Equal(3,(await catalog.CreateSnapshot(filter,1,1)).Count);
        Assert.Equal(3,(await catalog.CreateSnapshot(filter with{Grouping=new(true)},1,2)).Count);
        var preview=await catalog.PreviewDirectoryRules("benchmark",filter.DirectoryRules);Assert.Equal(2,preview.HiddenFiles);Assert.Equal(3,preview.VisibleFiles);
        Assert.Equal(5,(await catalog.ReadFirstPage(filter with{DirectoryRules=[]})).Items.Count);
        var saved=System.Text.Json.JsonSerializer.Deserialize<FilterSpec>(System.Text.Json.JsonSerializer.Serialize(filter))!;
        Assert.Equal(filter.DirectoryRules,saved.DirectoryRules);
        await catalog.Write(c=>{using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Files SET extension='.XLSX',kind='other',format_id=NULL WHERE entry_id='000000000001'";return cmd.ExecuteNonQuery();});
        var excel=filter with{DirectoryRules=[],Kinds=[],Extensions=FileCategories.Extensions("spreadsheets")};
        Assert.Single((await catalog.ReadFirstPage(excel)).Items);Assert.Equal(1,(await catalog.CreateSnapshot(excel,1,3)).Count);
        Assert.Equal("spreadsheets",FileCategories.FromFilter(excel));
        Assert.Empty((await catalog.ReadFirstPage(excel with{Extensions=FileCategories.Extensions("pdf")})).Items);
    }
}
