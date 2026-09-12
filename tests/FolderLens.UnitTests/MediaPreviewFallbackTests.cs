using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class MediaPreviewFallbackTests
{
    [Theory]
    [InlineData("DecodeFailed",true)][InlineData("UnsupportedCodec",true)]
    [InlineData("Timeout",true)][InlineData("ResourceLimit",true)][InlineData("OutOfMemory",true)]
    [InlineData("FileChanged",false)][InlineData("AccessDenied",false)]
    [InlineData("ProtocolViolation",false)][InlineData("Invalid asset token.",false)]
    [InlineData("Offline",false)][InlineData("DependencyUnavailable",false)]
    public void OnlyDecodeAndCapacityFailuresRetainCameraPreview(string code,bool retain)=>
        Assert.Equal(retain,MediaPreviewFallback.CanRetainRawPreview(new InvalidDataException(code)));

    [Fact] public void CancellationAndSourceIoAreNotCameraPreviewFallbacks()
    {
        Assert.True(MediaPreviewFallback.CanRetainRawPreview(new TimeoutException()));
        Assert.True(MediaPreviewFallback.CanRetainRawPreview(new WorkerResourceLimitException("budget")));
        Assert.False(MediaPreviewFallback.CanRetainRawPreview(new OperationCanceledException()));
        Assert.False(MediaPreviewFallback.CanRetainRawPreview(new IOException("FileChanged")));
        Assert.False(MediaPreviewFallback.CanRetainRawPreview(new UnauthorizedAccessException()));
    }
}
