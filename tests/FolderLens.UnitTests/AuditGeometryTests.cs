using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class AuditGeometryTests
{
    [Theory]
    [InlineData("width")]
    [InlineData("ratio")]
    [InlineData("orientation")]
    public void AllFilesGeometryIncludesVideoAndHonorsImageOnlyCategory(string condition)
    {
        using var connection=new SqliteConnection("Data Source=:memory:");connection.Open();
        using(var setup=connection.CreateCommand())
        {
            setup.CommandText="CREATE TABLE Files(root_id TEXT,entry_state TEXT,file_attributes INTEGER,kind TEXT,display_width INTEGER,display_height INTEGER); INSERT INTO Files VALUES('r','present',0,'image',1920,1080),('r','present',0,'video',1920,1080),('r','present',0,'audio',NULL,NULL);";setup.ExecuteNonQuery();
        }
        var filter=new FilterSpec{RootId="r",Kinds=[]};
        filter=condition switch{"width"=>filter with{Ranges=new(){["width"]=new(1280,null)}},"ratio"=>filter with{AspectRatio=new(1.5,null)},_=>filter with{Orientation="landscape"}};
        int Count(FilterSpec specification)
        {
            var query=FilterSql.Build(specification);using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM Files f WHERE "+query.MatchExpression;
            foreach(var parameter in query.Parameters)command.Parameters.AddWithValue(parameter.Key,parameter.Value);
            return Convert.ToInt32(command.ExecuteScalar());
        }
        Assert.Equal(2,Count(filter));Assert.Equal(1,Count(filter with{Kinds=["image"]}));Assert.Equal(0,Count(filter with{Kinds=["audio"]}));
    }
}
