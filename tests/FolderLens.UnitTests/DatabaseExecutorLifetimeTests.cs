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
    [Fact]
    public async Task FailingDiagnosticsCallbackDoesNotStopTheDatabaseThread()
    {
        string path=Path.Combine(Path.GetTempPath(),"FolderLens-db-measure",Guid.NewGuid().ToString("N"),"test.sqlite");
        await using var db=new DatabaseExecutor(path);
        int owner=await db.Execute(_=>Environment.CurrentManagedThreadId);
        var previous=DatabaseExecutor.OperationMeasured;
        // Only this executor's thread observes the failing callback.
        DatabaseExecutor.OperationMeasured=(_,_)=>{if(Environment.CurrentManagedThreadId==owner)throw new InvalidOperationException("diagnostic sink failed");previous?.Invoke("",0);};
        try
        {
            Assert.Equal(1,await db.Execute(_=>1).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2,await db.Execute(_=>2).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally{DatabaseExecutor.OperationMeasured=previous;}
    }
}
