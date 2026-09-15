namespace FolderLens.Infrastructure;

public sealed record ApprovedTextInput(string Path,long? ExpectedLength=null,long? ExpectedLastWriteTicks=null,bool AllowCloud=false);
public sealed record TextWorkerParameters(string? Encoding=null,long ByteOffset=0,int MaxBytes=64*1024,string? Literal=null,
    bool MatchCase=true,bool Wrap=true,string? SearchToken=null,long LineNumber=1,int StepPages=4,int MaxHits=1,TextFileSnapshot? ExpectedSnapshot=null);
public sealed record TextWindowData(long Start,long Next,long Length,string Text,string Encoding,bool AtEnd,int[] OriginalByteOffsets);
public sealed record TextWorkerResponse(TextFileSnapshot Snapshot,string Encoding,string VersionKey,TextWindowData? Window=null,
    TextIndexProgress? Index=null,TextPosition? Position=null,bool LineResolved=false,TextSearchBatch? Search=null,bool IsFinal=true);
public sealed record RemoteTextFindResult(TextMatch? Match,bool Wrapped,bool SearchExhausted,long ScannedBytes,string VersionKey);
