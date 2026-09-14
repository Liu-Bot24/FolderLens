using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FolderLens.Infrastructure;

/// <summary>A bounded, dedicated database thread; no synchronous SQLite execution escapes to the UI.</summary>
public sealed class DatabaseExecutor : IAsyncDisposable
{
    public static Action<string,double>? OperationMeasured {get;set;}
    private readonly Channel<Action<SqliteConnection>> queue = Channel.CreateBounded<Action<SqliteConnection>>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    public DatabaseExecutor(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        thread = new(() => Run(databasePath)) { IsBackground = true, Name = "FolderLens SQLite" };
        thread.Start();
    }
    private void Run(string path)
    {
        try
        {
            using (var connection = Open(path))
            {
                ready.TrySetResult();
                while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    while (queue.Reader.TryRead(out var action)) action(connection);
            }
            // Closing SQLite may checkpoint/delete its WAL and shared-memory files.
            // Do not release owners or let a replacement reopen until that has finished.
            stopped.TrySetResult();
        }
        catch (Exception ex) { ready.TrySetException(ex); stopped.TrySetException(ex); queue.Writer.TryComplete(ex); }
    }
    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            connection.CreateFunction("lens_location",(string? root,string relative,string mode)=>root is null?null:CatalogStore.LocationKey(root,relative,mode),true);
            connection.CreateFunction("lens_file_location",(string root,string relative,string mode,string? parent,string? file,string entry)=>CatalogStore.FileLocationKey(root,relative,mode,parent,file,entry),true);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE; PRAGMA cache_size=-32768; PRAGMA journal_size_limit=4194304;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public async Task<T> Execute<T>(Func<SqliteConnection, T> action, CancellationToken cancellation = default)
    {
        await ready.Task.WaitAsync(cancellation).ConfigureAwait(false);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(connection =>
        {
            if (cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); return; }
            using var registration = cancellation.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
            var measured=OperationMeasured;long started=measured is null?0:System.Diagnostics.Stopwatch.GetTimestamp();
            try { var result = action(connection); if(cancellation.IsCancellationRequested){if(result is IDisposable disposable)disposable.Dispose();cancellation.ThrowIfCancellationRequested();} completion.TrySetResult(result); }
            catch (Exception) when (cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { measured?.Invoke(action.Method.DeclaringType?.FullName+"."+action.Method.Name,System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        }, cancellation).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await stopped.Task.ConfigureAwait(false); }
}
