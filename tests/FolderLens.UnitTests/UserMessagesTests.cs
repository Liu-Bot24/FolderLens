using FolderLens.App;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class UserMessagesTests
{
    [Theory]
    [InlineData("FileChanged", "文件已更改")]
    [InlineData("DecodeFailed", "无法读取")]
    [InlineData("UnsupportedCodec", "不支持")]
    public void ProtocolErrorsAreExplainedWithoutChangingTheException(string code,string expected)
    {
        var error=new InvalidDataException(code);
        Assert.Contains(expected,UserMessages.Error(error));
        Assert.DoesNotContain(code,UserMessages.Error(error));
        Assert.Equal(code,error.Message);
    }
    [Fact]
    public void ActionableValidationIsKeptButTechnicalDetailsAreNot()
    {
        Assert.Equal("最大宽度不能小于最小宽度。",UserMessages.Error(new ArgumentException("最大宽度不能小于最小宽度。")));
        Assert.DoesNotContain("SELECT",UserMessages.Error(new IOException("SQL error: SELECT * FROM Files")));
        Assert.DoesNotContain("预算",UserMessages.Error(new IOException("工作进程临时磁盘预算已触发，任务已停止。")));
    }
    [Fact]
    public void IncompleteInformationIsNotReportedAsAMatchOrAnEmptyFolder()
    {
        string pending=UserMessages.Results(2,3,1,true);
        Assert.Contains("已显示 2",pending);Assert.Contains("3 个文件的信息尚未读全",pending);Assert.Contains("1 个文件的信息无法读取",pending);
        Assert.Equal("已显示 2 个文件",UserMessages.Results(2,0,0,false));
    }
}
