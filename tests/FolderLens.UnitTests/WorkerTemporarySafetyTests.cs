using System.Security.AccessControl;
using System.Security.Principal;
using FolderLens.Infrastructure;
using Xunit;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FolderLens.UnitTests;

public sealed class WorkerTemporarySafetyTests
{
    private static string Fixture()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-temporary-audit",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);return root;
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void SamplingAllowsAnEnumeratedEntryToDisappear(bool directory)
    {
        using var session=WorkerTemporaryFiles.Create(Fixture(),Guid.NewGuid().ToString("N"));
        string entry=Path.Combine(session.Path,directory?"ordinary-directory":"ordinary.tmp");
        if(directory)Directory.CreateDirectory(entry);else File.WriteAllText(entry,"ordinary generated data");
        session.BeforeInspectEntry=path=>{if(path!=entry)return;if(directory)Directory.Delete(path,false);else File.Delete(path);};
        Assert.InRange(session.CheckBudget(1024*1024),0,1024*1024);
    }
    [Fact]
    public void DirectoryPinPreventsRenameUntilDisposed()
    {
        string root=Fixture(),directory=Path.Combine(root,"pinned"),renamed=Path.Combine(root,"renamed");Directory.CreateDirectory(directory);
        Assert.StartsWith(root+Path.DirectorySeparatorChar,Path.GetFullPath(renamed));
        using(var pin=ThumbnailCache.PinDirectory(directory))Assert.Throws<IOException>(()=>Directory.Move(directory,renamed));
        Directory.Move(directory,renamed);Assert.True(Directory.Exists(renamed));
    }
    [Fact]
    public void DirectoryPinDoesNotRequireListingPermission()
    {
        string directory=Fixture();var info=new DirectoryInfo(directory);var original=info.GetAccessControl();
        var restricted=info.GetAccessControl();var user=WindowsIdentity.GetCurrent().User!;
        restricted.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.ListDirectory,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Deny));
        try
        {
            info.SetAccessControl(restricted);
            Assert.Throws<UnauthorizedAccessException>(()=>Directory.GetFileSystemEntries(directory));
            using var pin=ThumbnailCache.PinDirectory(directory);Assert.False(pin.IsInvalid);
        }
        finally{info.SetAccessControl(original);}
    }
    [Fact]
    public void ByteBudgetRemainsEnforced()
    {
        using var session=WorkerTemporaryFiles.Create(Fixture(),Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(Path.Combine(session.Path,"ordinary.tmp"),new byte[4096]);
        Assert.Throws<WorkerResourceLimitException>(()=>session.CheckBudget(1024));
    }
    [Theory]
    [InlineData(2,true)][InlineData(3,true)][InlineData(5,false)][InlineData(32,false)][InlineData(33,false)]
    public void OnlyExplicitMissingErrorsAreIgnored(int code,bool missing)
    {
        Assert.Equal(missing,WorkerTemporaryFiles.IsMissingEntry(new Win32Exception(code)));
        Assert.Equal(missing,WorkerTemporaryFiles.IsMissingEntry(new IOException("fixture",unchecked((int)0x80070000)|code)));
    }
    [Fact]
    public void FileReplacedByAHardLinkIsRejected()
    {
        string root=Fixture(),outside=Path.Combine(root,"other.tmp");File.WriteAllText(outside,"generated fixture");
        using var session=WorkerTemporaryFiles.Create(root,Guid.NewGuid().ToString("N"));
        string entry=Path.Combine(session.Path,"ordinary.tmp");File.WriteAllText(entry,"temporary");
        session.BeforeInspectEntry=path=>{if(path!=entry)return;File.Delete(path);Assert.True(CreateHardLinkW(path,outside,IntPtr.Zero));};
        Assert.Throws<WorkerTemporaryAccessException>(()=>session.CheckBudget(1024*1024));
    }
    [Fact]
    public void DirectoryDepthLimitRemainsAResourceError()
    {
        using var session=WorkerTemporaryFiles.Create(Fixture(),Guid.NewGuid().ToString("N"));
        string directory=session.Path;for(int i=0;i<14;i++){directory=Path.Combine(directory,"nested");Directory.CreateDirectory(directory);}
        Assert.Throws<WorkerResourceLimitException>(()=>session.CheckBudget(1024*1024));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
}
