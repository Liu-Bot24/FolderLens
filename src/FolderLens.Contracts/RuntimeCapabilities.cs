namespace FolderLens.Contracts;

public sealed record FormatRuntimeCapability(string Format,string Provider,bool ProviderAvailable,string BasicDecodeStatus,string? FixtureSha256,string? ErrorCode,string FullMatrixStatus="NOT_RUN");
public sealed record RuntimeCapabilities(string BuildId,string VipsVersion,string MagickVersion,int? RawBridgeAbi,DateTimeOffset CheckedUtc,IReadOnlyList<FormatRuntimeCapability> Formats);
