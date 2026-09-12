using FolderLens.Contracts;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ApprovedInputTests
{
    [Fact]public void UnindexedObservationPinsBothFieldsAndIndexedObservationCannotRefreshThem()
    {
        var input=new ApprovedInput(@"C:\source.jpg");var observed=input.Observe(100,200);
        Assert.Null(input.Length);Assert.Null(input.LastWriteTicks);
        Assert.Equal(100,observed.Length);Assert.Equal(200,observed.LastWriteTicks);
        Assert.Equal(observed,observed.Observe(100,200));
        Assert.Equal("FileChanged",Assert.Throws<IOException>(()=>observed.Observe(101,200)).Message);
        Assert.Equal("FileChanged",Assert.Throws<IOException>(()=>observed.Observe(100,201)).Message);
    }
    [Fact]public void IncompleteOrInvalidVersionCannotDisableTheIndexedGuard()
    {
        foreach(var input in new[]{new ApprovedInput("path",1,null),new ApprovedInput("path",null,1),new ApprovedInput("path",-1,1),new ApprovedInput("path",1,-1)})
            Assert.Throws<InvalidDataException>(()=>input.Observe(1,1));
        Assert.Throws<InvalidDataException>(()=>new ApprovedInput("path").Observe(-1,1));
    }
}
