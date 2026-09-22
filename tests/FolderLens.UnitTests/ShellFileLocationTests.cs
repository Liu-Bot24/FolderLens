using System.Runtime.InteropServices;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ShellFileLocationTests
{
    [Theory]
    [InlineData("plain.jpg")]
    [InlineData("有 空格,逗号 & 括号(1) ❤️.jpg")]
    [InlineData("a",true)]
    public async Task ExactFilePidlReachesSelectionApi(string name,bool longPath=false)
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-reveal",Guid.NewGuid().ToString("N"));
        string parent=longPath?Path.Combine(root,new string('a',120),new string('b',120)):root;
        Directory.CreateDirectory(parent);string file=Path.Combine(parent,name);File.WriteAllText(file,"fixture");
        try
        {
            int calls=0;
            await ShellFileLocation.RevealCore(file,pidl=>
            {
                calls++;Assert.Equal(ApartmentState.STA,Thread.CurrentThread.GetApartmentState());
                Assert.Equal(file,PathOf(pidl));return 0;
            });
            Assert.Equal(1,calls);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task MissingFileAndCanceledRequestNeverOpenExplorer()
    {
        string missing=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"missing.txt");int calls=0;
        await Assert.ThrowsAnyAsync<IOException>(()=>ShellFileLocation.RevealCore(missing,_=>{calls++;return 0;}));
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ShellFileLocation.RevealCore(missing,_=>{calls++;return 0;},stop.Token));
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task NativeSelectionFailureIsReportedWithoutFallback()
    {
        string file=Path.GetTempFileName();int calls=0;
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>ShellFileLocation.RevealCore(file,_=>{calls++;return unchecked((int)0x80070005);}));
            Assert.Equal(1,calls);
        }
        finally{File.Delete(file);}
    }

    [Fact]
    public async Task CanceledQueuedRequestCannotOpenAfterEarlierRequestCompletes()
    {
        string file=Path.GetTempFileName();using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int obsoleteCalls=0;
        Task first=ShellFileLocation.RevealCore(file,_=>{entered.SetResult();return release.Wait(TimeSpan.FromSeconds(5))?0:unchecked((int)0x80004005);});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var stop=new CancellationTokenSource();
            Task obsolete=ShellFileLocation.RevealCore(file,_=>{obsoleteCalls++;return 0;},stop.Token);
            stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>obsolete);
            release.Set();await first;Assert.Equal(0,obsoleteCalls);
        }
        finally{release.Set();await first;File.Delete(file);}
    }

    private static string PathOf(nint pidl)
    {
        Marshal.ThrowExceptionForHR(SHGetNameFromIDList(pidl,0x80058000,out nint value));
        try{return Marshal.PtrToStringUni(value)!;}finally{Marshal.FreeCoTaskMem(value);}
    }
    [DllImport("shell32.dll")]private static extern int SHGetNameFromIDList(nint pidl,uint kind,out nint value);
}
