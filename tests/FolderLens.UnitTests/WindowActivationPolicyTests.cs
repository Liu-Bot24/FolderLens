using FolderLens.Core;
using FolderLens.Infrastructure;
using System.Text.Json;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class WindowActivationPolicyTests
{
    [Theory]
    [InlineData(11,11,22,true)]
    [InlineData(11,33,22,false)]
    [InlineData(11,22,22,true)]
    [InlineData(0,33,22,false)]
    [InlineData(11,0,22,false)]
    public void DelayedRequestsRespectCurrentForeground(long requested,long current,long target,bool expected)=>
        Assert.Equal(expected,WindowActivationPolicy.CanActivate(requested,current,target));

    [Fact] public void ForwardedRequestsRetainForegroundAndOldRequestsDefaultToNoActivation()
    {
        var request=ActivationRequest.Parse(["--root",@"D:\photos"]) with{RequestedForeground=12345};
        Assert.Equal(request,JsonSerializer.Deserialize<ActivationRequest>(JsonSerializer.Serialize(request)));
        Assert.Equal(0,JsonSerializer.Deserialize<ActivationRequest>("{\"Root\":null,\"File\":null}")!.RequestedForeground);
    }
}
