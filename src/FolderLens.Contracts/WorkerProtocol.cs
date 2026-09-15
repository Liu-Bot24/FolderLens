using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderLens.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RequestContext(string RootId,long RootEpoch,long QueryGeneration,long SelectionGeneration,long FileVersion,long PathRevision,string? ResultSessionId=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InputReference(string EntryId,string InputToken);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceBudget(long MemoryBytes=2L*1024*1024*1024,long TempBytes=1024L*1024*1024,long CpuThreads=4,long MaxPixels=300_000_000,long MaxOutputBytes=128L*1024*1024);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImageParameters(int TargetWidth=1920,int TargetHeight=1080,int FrameIndex=0,int TileX=0,int TileY=0,int TileSize=1024,int Level=0,int PageIndex=0,long CompletedLoops=0,bool ReadDetails=true);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApprovedInput(string Path,long? Length=null,long? LastWriteTicks=null,bool AllowCloud=false)
{
    public static void CheckAccess(FileAttributes attributes,bool allowCloud)
    {
        if(!allowCloud&&((long)attributes&(0x1000|0x40000|0x400000))!=0)throw new IOException("CloudReadNotApproved");
    }
    // A missing pair asks the isolated worker to observe the source. A supplied
    // pair is an immutable indexed version and must never be silently refreshed.
    public ApprovedInput Observe(long length,long lastWriteTicks)
    {
        if(Length.HasValue!=LastWriteTicks.HasValue||Length<0||LastWriteTicks<0||length<0||lastWriteTicks<0)
            throw new InvalidDataException("Invalid source version.");
        if(Length is {} expectedLength&&(expectedLength!=length||LastWriteTicks!=lastWriteTicks))throw new IOException("FileChanged");
        return this with{Length=length,LastWriteTicks=lastWriteTicks};
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerEnvelope
{
    public int ProtocolMajor {get;init;}=1;
    public int ProtocolMinor {get;init;}
    public string Type {get;init;}="request";
    public string MessageId {get;init;}=Guid.NewGuid().ToString("N");
    public string WorkerInstanceId {get;init;}="";
    public string? RequestId {get;init;}
    public string? BuildId {get;init;}
    public string? Nonce {get;init;}
    public RequestContext? Context {get;init;}
    public string? Operation {get;init;}
    public InputReference? FileRef {get;init;}
    public DateTimeOffset? DeadlineUtc {get;init;}
    public ResourceBudget? ResourceBudget {get;init;}
    public JsonElement? Parameters {get;init;}
    public string? Status {get;init;}
    public string? ErrorCode {get;init;}
    public string? Quality {get;init;}
    public string? AssetToken {get;init;}
    public JsonElement? Metadata {get;init;}
    public string? LeaseId {get;init;}
}
public static class WorkerProtocol
{
    public const int MaxControlBytes=1024*1024;
    public const string BuildId="folderlens-0.1.4-demand-metadata";
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull,MaxDepth=32};
    public static async Task Write(Stream stream,WorkerEnvelope message,CancellationToken cancellation=default)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(message,Json);if(bytes.Length>MaxControlBytes)throw new InvalidDataException("Control frame budget exceeded.");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);await stream.WriteAsync(header,cancellation);await stream.WriteAsync(bytes,cancellation);await stream.FlushAsync(cancellation);
    }
    public static async Task<WorkerEnvelope> Read(Stream stream,CancellationToken cancellation=default)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);int length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length is <2 or >MaxControlBytes)throw new InvalidDataException("Invalid control frame length.");
        byte[] bytes=new byte[length];await stream.ReadExactlyAsync(bytes,cancellation);var result=JsonSerializer.Deserialize<WorkerEnvelope>(bytes,Json)??throw new InvalidDataException("Empty protocol message.");
        if(result.ProtocolMajor!=1 || result.ProtocolMinor<0 || string.IsNullOrEmpty(result.WorkerInstanceId) || result.MessageId.Length is <1 or >128 || result.Type is not ("hello" or "request" or "response" or "cancel" or "releaseLease" or "ackLease"))throw new InvalidDataException("Protocol violation.");
        return result;
    }
    public static bool SafeToken(string token)=>token.Length==32 && token.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
