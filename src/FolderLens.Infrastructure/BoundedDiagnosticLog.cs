using System.Text.Json;
using System.Threading.Channels;

namespace FolderLens.Infrastructure;

/// <summary>Non-blocking, bounded local diagnostic events. Callers pass identifiers
/// and counters. Explicit local development diagnostics may contain source paths
/// and errors; never include media or credentials, or upload these logs.</summary>
public sealed class BoundedDiagnosticLog : IAsyncDisposable
{
    private readonly Channel<object> events=Channel.CreateBounded<object>(new BoundedChannelOptions(128){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    private readonly Task writer;
    public BoundedDiagnosticLog(string directory)
    {
        writer=Task.Run(async()=>
        {
            try
            {
                Directory.CreateDirectory(directory);string path=Path.Combine(directory,"scan.jsonl");
                await foreach(var entry in events.Reader.ReadAllAsync())
                {
                    if(File.Exists(path)&&new FileInfo(path).Length>=512*1024)File.Move(path,path+".previous",true);
                    string line=JsonSerializer.Serialize(entry);
                    if(line.Length>16384)line=JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,kind="oversized-event",characters=line.Length});
                    await File.AppendAllTextAsync(path,line+Environment.NewLine);
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Failure=ex.GetType().Name;}
        });
    }
    public string? Failure {get;private set;}
    public void Write(string kind,object details)=>events.Writer.TryWrite(new{utc=DateTimeOffset.UtcNow,kind,details});
    public async ValueTask DisposeAsync(){events.Writer.TryComplete();await writer.ConfigureAwait(false);}
}
