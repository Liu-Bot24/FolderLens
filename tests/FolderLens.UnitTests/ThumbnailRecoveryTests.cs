using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ThumbnailRecoveryTests
{
    [Theory]
    [InlineData(-100009,true)][InlineData(-100008,false)][InlineData(-100007,false)][InlineData(-2,false)]
    public void RawIoIsDistinctFromCorruptionAndUnsupportedFiles(int code,bool retryable)
    {
        var error=Record.Exception(()=>FolderLens.Media.Worker.RawDecoder.ThrowIfError(code,"fixture"));
        Assert.NotNull(error);Assert.Equal(retryable,ThumbnailRecovery.CanRetry(error));
        FolderLens.Media.Worker.RawDecoder.ThrowIfError(0,"fixture");
    }
    [Fact]
    public async Task ExcludedSourceProbePreservesItsNonRetryableReason()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-excluded-retry",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        string source=Path.Combine(directory,"offline.mp4");File.WriteAllText(source,"fixture");File.SetAttributes(source,FileAttributes.Archive|FileAttributes.Offline);
        try
        {
            await using var probe=new SourceFileProbe();
            var error=await Assert.ThrowsAsync<IOException>(()=>probe.ReadObservation(source,default));
            Assert.Equal("DeferredOffline",error.Message);Assert.False(ThumbnailRecovery.CanRetry(error));
        }
        finally{File.SetAttributes(source,FileAttributes.Normal);}
    }
    [Fact]
    public void RetriesWaitBetweenFailuresAndStopAtThreeMinutes()
    {
        var recovery=new ThumbnailRecovery(TimeSpan.Zero);
        Assert.True(recovery.Failed(TimeSpan.Zero));
        Assert.False(recovery.TryBegin(TimeSpan.FromSeconds(4.999)));
        for(int second=5;second<180;second+=5)
        {
            var now=TimeSpan.FromSeconds(second);
            Assert.True(recovery.TryBegin(now));Assert.False(recovery.TryBegin(now));
            Assert.True(recovery.Failed(now));
            Assert.False(recovery.TryBegin(now+TimeSpan.FromSeconds(4.999)));
        }
        Assert.False(recovery.TryBegin(TimeSpan.FromMinutes(3)));
        Assert.False(recovery.Failed(TimeSpan.FromMinutes(3)));
        Assert.False(recovery.Pending);
    }
    [Fact]
    public void SlowAttemptsAndTimeOffscreenDoNotResetWindow()
    {
        var start=TimeSpan.FromDays(20);var recovery=new ThumbnailRecovery(start);
        Assert.True(recovery.Failed(start));Assert.True(recovery.TryBegin(start+TimeSpan.FromSeconds(5)));
        Assert.True(recovery.Failed(start+TimeSpan.FromSeconds(65)));
        Assert.False(recovery.TryBegin(start+TimeSpan.FromSeconds(69)));
        Assert.False(recovery.TryBegin(start+TimeSpan.FromMinutes(4)));
        Assert.False(recovery.Failed(start+TimeSpan.FromMinutes(4)));
    }
    [Theory]
    [InlineData("IoUnavailable",true)][InlineData("SourceIoError",true)][InlineData("PathNotFound",true)]
    [InlineData("FileChanged",false)][InlineData("AccessDenied",false)][InlineData("DeferredOffline",false)]
    [InlineData("LinkSkipped",false)][InlineData("CloudReadNotApproved",false)]
    public void SourceFailuresKeepTheirClassification(string code,bool retry)=>Assert.Equal(retry,ThumbnailRecovery.CanRetry(new IOException(code)));
    [Fact]
    public void DeadlinesRecoverButCancellationPermissionsAndInvalidContentDoNot()
    {
        Assert.True(ThumbnailRecovery.CanRetry(new TimeoutException()));
        Assert.True(ThumbnailRecovery.CanRetry(new FileNotFoundException()));
        Assert.True(ThumbnailRecovery.CanRetry(new InvalidDataException("Timeout")));
        Assert.False(ThumbnailRecovery.CanRetry(new OperationCanceledException()));
        Assert.False(ThumbnailRecovery.CanRetry(new UnauthorizedAccessException()));
        Assert.False(ThumbnailRecovery.CanRetry(new InvalidDataException("DecodeFailed")));
        Assert.False(ThumbnailRecovery.CanRetry(new NotSupportedException()));
        Assert.False(ThumbnailRecovery.CanRetry(new CloudFileRequiresApprovalException()));
        Assert.False(ThumbnailRecovery.CanRetry(new WorkerTemporaryAccessException("unsafe")));
        Assert.False(ThumbnailRecovery.CanRetry(new IOException("disk full",unchecked((int)0x80070070))));
    }
}
