using FolderLens.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class CatalogBackupTests
{
    [Fact]
    public void IncrementalBackupCancelsBetweenPagesAndSuccessfulRetryIsConsistent()
    {
        string directory=Path.Combine(Path.GetTempPath(),"FolderLens-backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using var source=DatabaseExecutor.Open(Path.Combine(directory,"source.sqlite"));
        using(var command=source.CreateCommand()){command.CommandText="CREATE TABLE Payload(value BLOB);INSERT INTO Payload VALUES(zeroblob(8388608))";command.ExecuteNonQuery();}
        using var destination=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"backup.sqlite"),Pooling=false}.ToString());destination.Open();
        using var stop=new CancellationTokenSource();int steps=0;
        Assert.ThrowsAny<OperationCanceledException>(()=>CatalogBackup.Copy(source,destination,stop.Token,(_,_)=>{steps++;stop.Cancel();}));
        Assert.Equal(1,steps);
        CatalogBackup.Copy(source,destination,CancellationToken.None);
        using var check=destination.CreateCommand();check.CommandText="SELECT length(value) FROM Payload";Assert.Equal(8388608L,check.ExecuteScalar());
        check.CommandText="PRAGMA integrity_check";Assert.Equal("ok",check.ExecuteScalar());
    }
}
