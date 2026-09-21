using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Infrastructure;

namespace FolderLens.Content.Worker;

/// <summary>All source opens, metadata calls, alignment, indexing and search execute inside this killable process.</summary>
internal sealed class ContentTextSession(string taskDirectory) : IAsyncDisposable
{
    private string? path,selectedEncoding,searchToken;
    private long fileVersion;
    private BoundedTextReader? reader;
    private TextLineIndex? lines;
    private IEnumerator<TextSearchBatch>? search;
    private CancellationTokenSource? searchCancellation;
    private TextWorkerParameters? searchParameters;
    private TextSearchBatch? finishedSearch;
    public static bool Supports(string? operation)=>operation is "textExcerpt" or "textWindow" or "textFind" or "textLinePosition" or "textIndexStep";
    private string Key=>reader!.Snapshot.Key(path!,reader.EncodingName,fileVersion);
    public async Task<TextWorkerResponse> Execute(WorkerEnvelope request)
    {
        if(request.Context is null || request.Parameters is null || request.FileRef is null || !WorkerProtocol.SafeToken(request.FileRef.InputToken))throw new InvalidDataException("InvalidTextRequest");
        var options=request.Parameters.Value.Deserialize<TextWorkerParameters>(WorkerProtocol.Json)??throw new InvalidDataException("InvalidTextRequest");
        if(options.MaxBytes is <16 or >64*1024 || options.StepPages is <1 or >16 || options.ByteOffset<0 || options.LineNumber<1 || options.MaxHits is <1 or >10_000)throw new InvalidDataException("TextRequestBudget");
        using var deadline=new CancellationTokenSource();
        var remaining=(request.DeadlineUtc??DateTimeOffset.UtcNow.AddSeconds(30))-DateTimeOffset.UtcNow;
        if(remaining<=TimeSpan.Zero)throw new TimeoutException("TextDeadline");deadline.CancelAfter(remaining);var cancellation=deadline.Token;
        string inputFile=Path.Combine(taskDirectory,request.FileRef.InputToken+".input.json");
        using var input=new FileStream(inputFile,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.Asynchronous);
        if(input.Length>256*1024)throw new InvalidDataException("TextInputTokenBudget");
        var approved=await JsonSerializer.DeserializeAsync<ApprovedTextInput>(input,WorkerProtocol.Json,cancellation)??throw new InvalidDataException("InvalidTextInput");
        if(!Path.IsPathFullyQualified(approved.Path)||approved.Path.Any(c=>char.IsControl(c)))throw new InvalidDataException("InvalidTextPath");
        string requestedPath=Path.GetFullPath(approved.Path);
        ApprovedInput.CheckAccess(File.GetAttributes(requestedPath),approved.AllowCloud);
        if(request.Operation=="textExcerpt")
        {
            if(options.MaxBytes>2048||options.ByteOffset!=0)throw new InvalidDataException("TextExcerptBudget");
            await CloseDocument();
            using var excerpt=new BoundedTextReader(requestedPath,options.Encoding,cancellation,
                new ApprovedInput(requestedPath,approved.ExpectedLength,approved.ExpectedLastWriteTicks,approved.AllowCloud,approved.SourceSignature),options.MaxBytes);
            if(options.ExpectedSnapshot is {} expected&&expected!=excerpt.Snapshot)throw new IOException("FileChanged");
            var page=excerpt.ReadWindow(0,options.MaxBytes,cancellation);
            return new(excerpt.Snapshot,excerpt.EncodingName,excerpt.Snapshot.Key(requestedPath,excerpt.EncodingName,request.Context.FileVersion),
                Window:new(page.Start,page.Next,page.Length,page.Text,page.Encoding,page.AtEnd,page.OriginalByteOffsets));
        }
        if(reader is null || path!=requestedPath || selectedEncoding!=options.Encoding || fileVersion!=request.Context.FileVersion)
        {
            await CloseDocument();path=requestedPath;selectedEncoding=options.Encoding;fileVersion=request.Context.FileVersion;
            reader=new(path,selectedEncoding,cancellation,new ApprovedInput(path,approved.ExpectedLength,approved.ExpectedLastWriteTicks,approved.AllowCloud,approved.SourceSignature));
        }
        ValidateExpected(approved,options.ExpectedSnapshot);reader.CheckVersion();
        TextWorkerResponse result;
        if(request.Operation=="textWindow")
        {
            var page=reader.ReadWindow(options.ByteOffset,options.MaxBytes,cancellation);
            result=new(reader.Snapshot,reader.EncodingName,Key,Window:new(page.Start,page.Next,page.Length,page.Text,page.Encoding,page.AtEnd,page.OriginalByteOffsets),Index:lines?.Progress);
        }
        else if(request.Operation=="textFind")
        {
            if(options.SearchToken is null || !WorkerProtocol.SafeToken(options.SearchToken)||string.IsNullOrEmpty(options.Literal))throw new InvalidDataException("InvalidTextSearch");
            if(searchToken!=options.SearchToken)
            {
                search?.Dispose();search=null;searchCancellation?.Dispose();searchCancellation=new();finishedSearch=null;searchToken=options.SearchToken;searchParameters=options;
                search=TextSearch.Scan(reader,new(options.Literal,options.MatchCase,options.ByteOffset,options.Wrap,options.MaxHits,Math.Min(32,options.MaxHits),request.Context.QueryGeneration,fileVersion),searchCancellation.Token).GetEnumerator();
            }
            else if(searchParameters is not {} previous || previous.Literal!=options.Literal || previous.MatchCase!=options.MatchCase || previous.ByteOffset!=options.ByteOffset || previous.Wrap!=options.Wrap || previous.MaxHits!=options.MaxHits)
                throw new InvalidDataException("TextSearchTokenReused");
            TextSearchBatch? batch=finishedSearch;
            if(batch is null)
            {
                using var interrupt=cancellation.Register(()=>searchCancellation!.Cancel());
                for(int n=0;n<options.StepPages;n++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(search is null || !search.MoveNext())throw new InvalidDataException("MissingTextSearchTerminal");
                    batch=search.Current with{VersionKey=Key};
                    if(batch.IsFinal){finishedSearch=batch;search.Dispose();search=null;break;}
                    if(batch.Matches.Count>0)break;
                }
            }
            result=new(reader.Snapshot,reader.EncodingName,Key,Search:batch,Index:lines?.Progress);
        }
        else
        {
            // The parent only grants this local task root; source content cannot select another write directory.
            lines??=await TextLineIndex.OpenAsync(path!,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(taskDirectory))!,"text-index"),selectedEncoding,fileVersion,cancellation);
            TextPosition? position=null;bool resolved=false;
            if(request.Operation=="textIndexStep")await lines.IndexStepAsync(options.StepPages,cancellation);
            else if(request.Operation=="textLinePosition")
            {
                if(lines.Progress.IndexedThroughLine<options.LineNumber && !lines.Progress.Complete)await lines.IndexStepAsync(options.StepPages,cancellation);
                if(lines.Progress.IndexedThroughLine>=options.LineNumber || lines.Progress.Complete)
                {position=await lines.EnsureLineAsync(options.LineNumber,cancellation:cancellation);resolved=true;}
            }
            else throw new InvalidDataException("UnsupportedTextOperation");
            result=new(reader.Snapshot,reader.EncodingName,Key,Index:lines.Progress,Position:position,LineResolved:resolved);
        }
        cancellation.ThrowIfCancellationRequested();ValidateExpected(approved,options.ExpectedSnapshot);reader.CheckVersion();return result;
    }
    private void ValidateExpected(ApprovedTextInput approved,TextFileSnapshot? expected)
    {
        var actual=reader!.Snapshot;
        if((approved.ExpectedLength is {} length && length!=actual.Length) ||
            (approved.SourceSignature is {Length:>0} signature && signature!=actual.SourceSignature) ||
            (approved.ExpectedLastWriteTicks is {} ticks && ticks!=DateTime.FromFileTimeUtc(actual.LastWriteTicks).Ticks) ||
            (expected is not null && expected!=actual))throw new IOException("FileChanged");
    }
    private async Task CloseDocument()
    {
        searchCancellation?.Cancel();search?.Dispose();search=null;searchCancellation?.Dispose();searchCancellation=null;finishedSearch=null;searchToken=null;searchParameters=null;
        if(lines is not null){await lines.DisposeAsync();lines=null;}reader?.Dispose();reader=null;
    }
    public ValueTask DisposeAsync()=>new(CloseDocument());
}
