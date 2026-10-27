using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class ProviderIdentityCancellationTests
{
    [Fact]
    public void CancelStopsTheProducerAndReleasesItsInput()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-identity-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"worker.exe");
        try
        {
            File.WriteAllBytes(path,new byte[1024*1024]);
            using var stop=new CancellationTokenSource();int reads=0;
            Assert.Throws<OperationCanceledException>(()=>WorkerClient.ComputeProviderIdentity(path,stop.Token,_=>{reads++;stop.Cancel();}));
            Assert.Equal(1,reads);
            using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Assert.Equal(1024*1024,exclusive.Length);}
            // The same thread is reused after cancellation: background priority
            // must have been restored, and the canceled hash must not be cached.
            string identity=WorkerClient.ComputeProviderIdentity(path,CancellationToken.None);
            Assert.StartsWith("sha256:",identity);
            File.WriteAllBytes(path,new byte[32]);
            Assert.NotEqual(identity,WorkerClient.ComputeProviderIdentity(path,CancellationToken.None));
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public void UnchangedComponentsReuseRememberedIdentityAndChangesRehash()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-identity-"+Guid.NewGuid().ToString("N"));
        string workers=Path.Combine(directory,"workers"),path=Path.Combine(workers,"worker.exe"),library=Path.Combine(workers,"codec.dll"),cache=Path.Combine(directory,"cache","provider-identity.json");
        Directory.CreateDirectory(workers);
        try
        {
            File.WriteAllBytes(path,new byte[4096]);File.WriteAllBytes(library,[1,2,3,4]);
            int reads=0;string first=WorkerClient.ComputeProviderIdentity(path,CancellationToken.None,_=>reads++,cache);
            Assert.True(reads>0);Assert.Equal(WorkerClient.ComputeProviderIdentity(path,CancellationToken.None),first);
            reads=0;Assert.Equal(first,WorkerClient.ComputeProviderIdentity(path,CancellationToken.None,_=>reads++,cache));Assert.Equal(0,reads);
            // Same length, new content: the write time changes and the identity is recomputed.
            File.WriteAllBytes(library,[4,3,2,1]);File.SetLastWriteTimeUtc(library,File.GetLastWriteTimeUtc(library).AddSeconds(1));
            string changed=WorkerClient.ComputeProviderIdentity(path,CancellationToken.None,_=>reads++,cache);
            Assert.True(reads>0);Assert.NotEqual(first,changed);Assert.Equal(WorkerClient.ComputeProviderIdentity(path,CancellationToken.None),changed);
            // A damaged remembered value falls back to hashing.
            File.WriteAllText(cache,"{");reads=0;
            Assert.Equal(changed,WorkerClient.ComputeProviderIdentity(path,CancellationToken.None,_=>reads++,cache));Assert.True(reads>0);
        }
        finally{Directory.Delete(directory,true);}
    }
}
