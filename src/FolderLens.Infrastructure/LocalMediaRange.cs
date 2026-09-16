using System.Globalization;
using FolderLens.Contracts;

namespace FolderLens.Infrastructure;

/// <summary>Bounded local video reads executed in the killable content worker.</summary>
public static class LocalMediaRange
{
    public const int MaximumBytes=256*1024;
    public static bool IsVideo(string path)=>ContentType(path) is not null;
    public static string? ContentType(string path)=>Path.GetExtension(Uri.UnescapeDataString(path)).ToLowerInvariant() switch
    {".mp4" or ".m4v"=>"video/mp4",".webm"=>"video/webm",".ogv"=>"video/ogg",_=>null};
    public static (long Start,int Count) Parse(string? range,long length)
    {
        if(length<=0)throw new InvalidDataException("MediaRangeNotSatisfiable");
        long start=0,end=length-1;
        if(!string.IsNullOrEmpty(range))
        {
            if(!range.StartsWith("bytes=",StringComparison.Ordinal)||range.Contains(','))throw new InvalidDataException("MediaRangeNotSatisfiable");
            var parts=range[6..].Split('-');
            bool Number(string value,out long number)=>long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out number);
            if(parts.Length!=2)throw new InvalidDataException("MediaRangeNotSatisfiable");
            if(parts[0].Length==0)
            {if(!Number(parts[1],out long suffix)||suffix<=0)throw new InvalidDataException("MediaRangeNotSatisfiable");start=Math.Max(0,length-suffix);}
            else
            {if(!Number(parts[0],out start)||parts[1].Length>0&&(!Number(parts[1],out end)||end<start))throw new InvalidDataException("MediaRangeNotSatisfiable");end=Math.Min(end,length-1);}
        }
        if(start>=length)throw new InvalidDataException("MediaRangeNotSatisfiable");
        return(start,(int)Math.Min(MaximumBytes,end-start+1));
    }
    public static object Read(ApprovedInput input,string? range)
    {
        if(!IsVideo(input.Path))throw new NotSupportedException();
        ApprovedInput.CheckAccess(File.GetAttributes(input.Path),input.AllowCloud);
        using var stream=new FileStream(input.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        input=input.Observe(FileReadObservation.Read(stream.SafeFileHandle));input.Observe(FileReadObservation.Read(input.Path));
        long length=stream.Length;var part=Parse(range,length);byte[] bytes=new byte[part.Count];stream.Position=part.Start;stream.ReadExactly(bytes);
        input.Observe(FileReadObservation.Read(stream.SafeFileHandle));input.Observe(FileReadObservation.Read(input.Path));
        return new{isFinal=true,start=part.Start,length,bytes,contentType=ContentType(input.Path)};
    }
}
