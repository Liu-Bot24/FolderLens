using System.Globalization;
using System.Text.RegularExpressions;

namespace FolderLens.Contracts;

public sealed record CaptureTimeInfo
{
    public string State {get;init;}="notRequested";
    public string? Source {get;init;}
    public string? Original {get;init;}
    public long? WallTicks {get;init;}
    public long? UtcTicks {get;init;}
    public int? OffsetMinutes {get;init;}
    public bool TimezoneUnknown {get;init;}
    public string? ErrorCode {get;init;}
    public static CaptureTimeInfo Missing(string? source=null)=>new(){State="unsupported",Source=source,ErrorCode="CaptureTimeMissing"};
    public static CaptureTimeInfo FromExif(string? original,string? offset,string? subsecond=null)
    {
        original=original?.Trim('\0',' ');offset=offset?.Trim('\0',' ');
        if(string.IsNullOrWhiteSpace(original))return Missing("EXIF DateTimeOriginal");
        var result=new CaptureTimeInfo{Source="EXIF DateTimeOriginal",Original=original.Length>64?original[..64]:original};
        if(!DateTime.TryParseExact(original,"yyyy:MM:dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var wall))return result with{State="failed",ErrorCode="InvalidCaptureTime"};
        subsecond=subsecond?.Trim('\0',' ');
        if(!string.IsNullOrEmpty(subsecond))
        {
            if(subsecond.Length>16||subsecond.Any(c=>c is <'0' or >'9'))return result with{State="failed",ErrorCode="InvalidCaptureSubsecond"};
            wall=wall.AddTicks(long.Parse(subsecond[..Math.Min(7,subsecond.Length)].PadRight(7,'0'),CultureInfo.InvariantCulture));
        }
        result=result with{WallTicks=wall.Ticks};
        if(string.IsNullOrEmpty(offset))return result with{State="unsupported",TimezoneUnknown=true,ErrorCode="CaptureTimezoneUnknown"};
        if(!Regex.IsMatch(offset,@"^[+-]\d{2}:\d{2}$",RegexOptions.CultureInvariant)||!int.TryParse(offset.AsSpan(1,2),out int hours)||!int.TryParse(offset.AsSpan(4,2),out int minutes)||minutes>59||hours*60+minutes>840)
            return result with{State="failed",TimezoneUnknown=true,ErrorCode="InvalidCaptureOffset"};
        int total=(hours*60+minutes)*(offset[0]=='-'?-1:1);
        try{return result with{State="ready",UtcTicks=new DateTimeOffset(DateTime.SpecifyKind(wall,DateTimeKind.Unspecified),TimeSpan.FromMinutes(total)).UtcTicks,OffsetMinutes=total};}
        catch(ArgumentException){return result with{State="failed",TimezoneUnknown=true,ErrorCode="InvalidCaptureOffset"};}
    }
    public static CaptureTimeInfo FromIso(string? original,string source)
    {
        if(string.IsNullOrWhiteSpace(original))return Missing(source);
        if(original.Length>64)return new(){State="failed",Source=source,ErrorCode="InvalidCaptureTime"};
        if(Regex.IsMatch(original,@"(?:Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant)&&DateTimeOffset.TryParse(original,CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))
            return new(){State="ready",Source=source,Original=original,WallTicks=time.DateTime.Ticks,UtcTicks=time.UtcTicks,OffsetMinutes=(int)time.Offset.TotalMinutes};
        string[] formats=["yyyy-MM-dd'T'HH:mm:ss","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if(DateTime.TryParseExact(original,formats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var wall))return new(){State="unsupported",Source=source,Original=original,WallTicks=wall.Ticks,TimezoneUnknown=true,ErrorCode="CaptureTimezoneUnknown"};
        return new(){State="failed",Source=source,Original=original,ErrorCode="InvalidCaptureTime"};
    }
}

public sealed record MediaStreamDetails(int Index,string Kind,string? Codec,int? Width,int? Height,int? Channels,int? SampleRate,bool IsDefault,bool AttachedPicture);
public sealed record ContentMetadataDetails
{
    public int SchemaVersion {get;init;}=1;
    public string State {get;init;}="notRequested";
    public string? ErrorCode {get;init;}
    public string? Provider {get;init;}
    public string? CameraMake {get;init;}
    public string? CameraModel {get;init;}
    public string? LensMake {get;init;}
    public string? LensModel {get;init;}
    public long? Iso {get;init;}
    public double? ExposureSeconds {get;init;}
    public string? ExposureRational {get;init;}
    public double? Aperture {get;init;}
    public double? ExposureBias {get;init;}
    public double? FocalLengthMm {get;init;}
    public int? FocalLength35Mm {get;init;}
    public int? EncodedWidth {get;init;}
    public int? EncodedHeight {get;init;}
    public int? ExifOrientation {get;init;}
    public int? BitDepth {get;init;}
    public bool? HasAlpha {get;init;}
    public string? SourceColorSpace {get;init;}
    public bool? HasIccProfile {get;init;}
    public string? IccDescription {get;init;}
    public string? IccSha256 {get;init;}
    public int? IccBytes {get;init;}
    public string ColorState {get;init;}="notRequested";
    public string? ColorErrorCode {get;init;}
    public long? FrameCount {get;init;}
    public long? PageCount {get;init;}
    public int? AnimationLoopCount {get;init;}
    public CaptureTimeInfo Capture {get;init;}=new();
    public long? BitRate {get;init;}
    public int? AudioChannels {get;init;}
    public int? SampleRate {get;init;}
    public double? RotationDegrees {get;init;}
    public string? SampleAspectRatio {get;init;}
    public string? DisplayAspectRatio {get;init;}
    public string? ColorPrimaries {get;init;}
    public string? ColorTransfer {get;init;}
    public bool? IsHdr {get;init;}
    public MediaStreamDetails[] Streams {get;init;}=[];
    public bool StreamsTruncated {get;init;}
}
