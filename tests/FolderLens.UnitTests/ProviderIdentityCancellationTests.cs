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
}
