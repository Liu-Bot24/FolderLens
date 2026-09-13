using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim queryDiagnosticGate=new(1,1);
    private sealed record QueryAttempt(long Request,long Generation,long Epoch,string RootId,bool Automatic,string FilterHash="");
    private static string QueryFilterHash(FolderLens.Core.FilterSpec filter)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(filter))));
    private async Task RecordQueryFailure(Exception error,string phase,QueryAttempt attempt)
    {
        var entry=new
        {
            utc=DateTimeOffset.UtcNow,attempt,phase,
            display=resultHandle is null?firstPageSequence.Length>0?"firstPage":"empty":"snapshot",
            firstPageCount=firstPageSequence.Length,displayedCount=resultHandle?.Count,
            snapshotId=resultHandle?.Id,errorType=error.GetType().Name,hresult=error.HResult,
            errorCode=error is Microsoft.Data.Sqlite.SqliteException sqlite?sqlite.SqliteExtendedErrorCode:(int?)null,
            cancelledByNewRequest=attempt.Request!=queryRequest,closing
        };
        string directory=dataDirectory;
        try
        {
            await queryDiagnosticGate.WaitAsync();
            try
            {
                await Task.Run(async()=>
                {
                    var sizes=new Dictionary<string,long>();
                    foreach(string name in new[]{"catalog.sqlite","catalog.sqlite-wal","sessions.sqlite","sessions.sqlite-wal"})
                    {var file=new FileInfo(Path.Combine(directory,"catalog",name));if(file.Exists)sizes[name]=file.Length;}
                    string path=Path.Combine(directory,"query-failures.jsonl");
                    if(File.Exists(path)&&new FileInfo(path).Length>512*1024)File.Move(path,path+".previous",true);
                    await File.AppendAllTextAsync(path,JsonSerializer.Serialize(new{query=entry,localDatabaseBytes=sizes})+Environment.NewLine);
                });
            }
            finally{queryDiagnosticGate.Release();}
        }
        catch(Exception diagnosticError) when(diagnosticError is IOException or UnauthorizedAccessException)
        {RecordWebView("Query diagnostic unavailable: "+diagnosticError.GetType().Name);}
    }
}
