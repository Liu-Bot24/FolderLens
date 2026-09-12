using System.Data;
using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class DatabaseExecutorLifetimeTests
{
    [Fact]
    public async Task DisposeWaitsUntilConnectionDisposalHasReturned()
    {
        string path=Path.Combine(Path.GetTempPath(),"FolderLens-db-close",Guid.NewGuid().ToString("N"),"test.sqlite");
        var db=new DatabaseExecutor(path);
        using var closing=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await db.Execute(connection=>
        {
            connection.StateChange+=(_,e)=>{if(e.CurrentState==ConnectionState.Closed){closing.Set();release.Wait(TimeSpan.FromSeconds(5));}};
            return true;
        });
        var disposing=db.DisposeAsync().AsTask();
        try
        {
            Assert.True(await Task.Run(()=>closing.Wait(TimeSpan.FromSeconds(5))));
            await Task.Delay(30);
            Assert.False(disposing.IsCompleted,"Shutdown completed while connection disposal was still running.");
        }
        finally{release.Set();await disposing;}
        await using var reopened=new DatabaseExecutor(path);
        Assert.Equal(1L,await reopened.Execute(c=>{using var command=c.CreateCommand();command.CommandText="SELECT 1";return (long)command.ExecuteScalar()!;}));
    }
}
