using System.Text;
using FolderLens.Core;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class TextAndStateTests
{
    private static string Fixture(string content, Encoding encoding)
    {
        string directory = Path.Combine(Path.GetTempPath(), "FolderLens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.txt");
        File.WriteAllText(path, content, encoding);
        return path;
    }
    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("x999999999999999999999", "x1000000000000000000000")]
    [InlineData("图2", "图20")]
    public void NaturalKeySortsWithoutOverflow(string first, string second) => Assert.True(NaturalOrder.Compare(first, second) < 0);
    [Fact] public void ExclusionUsesSegmentBoundary()
    {
        Assert.True(PathRules.IsWithinRelative(@"foo\a.jpg", "foo"));
        Assert.False(PathRules.IsWithinRelative(@"foobar\a.jpg", "foo"));
        Assert.False(PathRules.IsWithinRelative(@"Foo\a.jpg", "foo"));
    }
    [Fact] public void GenerationRejectsOldResult()
    {
        var context = new WorkContext(1, 2, 3, "a", 1, Guid.NewGuid());
        Assert.True(context.IsCurrent(context));
        Assert.False(context.IsCurrent(context with { SelectionGeneration = 4 }));
        Assert.False(context.IsCurrent(context with { WorkerInstanceId = Guid.NewGuid() }));
    }
    [Theory][InlineData("utf-8")][InlineData("utf-16")][InlineData("utf-16BE")][InlineData("gb18030")]
    public void WindowsReassembleExactly(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(name);
        string value = string.Concat(Enumerable.Repeat("你好𠮷😀\r\nFolderLens", 100));
        string path = Fixture(value, encoding);
        try
        {
            using var reader = new BoundedTextReader(path, name);
            long offset = encoding.GetPreamble().Length;
            var output = new StringBuilder();
            while (offset < reader.Length) { var page = reader.ReadWindow(offset, 19); output.Append(page.Text); Assert.True(page.Next > offset); offset = page.Next; }
            Assert.Equal(value, output.ToString());
        }
        finally { File.Delete(path); }
    }
    [Fact] public void SearchFindsCrossBlockUnicodeAndNotAbsentTerm()
    {
        string value = new string('a', 65534) + "中文😀needle" + new string('z', 65530) + "中文😀needle";
        string path = Fixture(value, new UTF8Encoding(false));
        try
        {
            using var reader = new BoundedTextReader(path);
            var hits = reader.Search("中文😀needle").ToArray();
            Assert.Equal(2, hits.Length); Assert.Equal(65534, hits[0].ByteOffset);
            Assert.Empty(reader.Search("absent"));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => reader.Search("none", cancellation: cancel.Token).ToArray());
        }
        finally { File.Delete(path); }
    }
    [Fact] public void RejectsTruncationAndBounds()
    {
        string path = Fixture(new string('x', 100), new UTF8Encoding(false));
        try { using var reader = new BoundedTextReader(path); Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadWindow(0, int.MaxValue)); File.WriteAllText(path, "a"); Assert.Throws<IOException>(() => reader.ReadWindow(0)); }
        finally { File.Delete(path); }
    }
}
