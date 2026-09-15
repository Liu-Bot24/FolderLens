using System.Text;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextAutoEncodingTests
{
    [Fact]
    public void AutomaticChineseTextReadsStrictGb18030WhenUtf8IsInvalid()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes=Encoding.GetEncoding("gb18030").GetBytes("中文说明\r\n地址 https://example.invalid/");
        var detected=TextEncodingPolicy.Detect(bytes,true);
        Assert.Equal("中文说明\r\n地址 https://example.invalid/",detected.Encoding.GetString(bytes));
        Assert.Equal(54936,detected.Encoding.CodePage);
    }
    [Fact]
    public void AutomaticFallbackDoesNotOverrideExplicitUtf8OrAcceptBinary()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes=Encoding.GetEncoding("gb18030").GetBytes("中文说明");
        Assert.Throws<InvalidDataException>(()=>TextEncodingPolicy.Detect(bytes,true,"utf-8"));
        Assert.Throws<InvalidDataException>(()=>TextEncodingPolicy.Detect([0x81,0x40,0,1,2],true));
        Assert.Equal(65001,TextEncodingPolicy.Detect(Encoding.UTF8.GetBytes("中文😀"),true).Encoding.CodePage);
    }
}
