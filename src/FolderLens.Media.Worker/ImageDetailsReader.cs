using System.Globalization;
using System.Security.Cryptography;
using FolderLens.Contracts;
using ImageMagick;

namespace FolderLens.Media.Worker;

internal static class ImageDetailsReader
{
    public static ContentMetadataDetails Read(Stream source,string format,string provider,int? frameCount,int? pageCount,int? loop)
    {
        long position=source.Position;
        var basic=new ContentMetadataDetails{Provider=provider,FrameCount=frameCount,PageCount=pageCount,AnimationLoopCount=loop};
        try
        {
            if(format!="jxl")return ExifDetailsReader.Read(source,format,basic);
            source.Position=0;using var image=new MagickImage();
            MagickFormat? coder=format switch
            {
                "jpeg"=>MagickFormat.Jpeg,"png" or "apng"=>MagickFormat.Png,"gif"=>MagickFormat.Gif,"webp"=>MagickFormat.WebP,"bmp"=>MagickFormat.Bmp,"ico"=>MagickFormat.Ico,
                "heic" or "heif" or "cr3"=>MagickFormat.Heic,"avif"=>MagickFormat.Avif,"jxl"=>MagickFormat.Jxl,
                "tiff" or "arw" or "dng" or "cr2" or "nef" or "rw2" or "orf" or "pef"=>MagickFormat.Tiff,_=>null
            };
            if(coder is null)return basic with{State="unsupported",ErrorCode="MetadataContainerUnsupported",Capture=new(){State="unsupported",ErrorCode="MetadataContainerUnsupported"},ColorState="unsupported",ColorErrorCode="MetadataContainerUnsupported"};
            image.Ping(source,new MagickReadSettings{Format=coder.Value,FrameIndex=0,FrameCount=1});
            var exif=image.GetExifProfile();
            object? Value(ExifTag tag)=>exif?.Values.FirstOrDefault(v=>v.Tag.ToString()==tag.ToString())?.GetValue();
            string? Text(ExifTag tag)=>Clean(Value(tag)?.ToString());
            double? Number(ExifTag tag)
            {
                object? value=Value(tag);if(value is Array array)value=array.Length>0?array.GetValue(0):null;
                double number=value switch{Rational rational=>rational.ToDouble(),SignedRational rational=>rational.ToDouble(),null=>double.NaN,_=>double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)?parsed:double.NaN};
                return double.IsFinite(number)?number:null;
            }
            long? iso=Number(ExifTag.ISOSpeed) is {} extended&&extended>0?(long)extended:Number(ExifTag.ISOSpeedRatings) is {} speed&&speed>0?(long)speed:null;
            double? exposure=Number(ExifTag.ExposureTime),aperture=Number(ExifTag.FNumber),focal=Number(ExifTag.FocalLength),equivalent=Number(ExifTag.FocalLengthIn35mmFilm);
            var details=basic with
            {
                State="ready",CameraMake=Text(ExifTag.Make),CameraModel=Text(ExifTag.Model),LensMake=Text(ExifTag.LensMake),LensModel=Text(ExifTag.LensModel),Iso=iso,
                ExposureSeconds=exposure>0?exposure:null,ExposureRational=exposure>0?Text(ExifTag.ExposureTime):null,Aperture=aperture>0?aperture:null,ExposureBias=Number(ExifTag.ExposureBiasValue),FocalLengthMm=focal>0?focal:null,FocalLength35Mm=equivalent is >0 and <=int.MaxValue?(int)equivalent:null,
                EncodedWidth=image.Width<=int.MaxValue?(int)image.Width:null,EncodedHeight=image.Height<=int.MaxValue?(int)image.Height:null,
                ExifOrientation=Number(ExifTag.Orientation) is >=1 and <=8?Convert.ToInt32(Number(ExifTag.Orientation)):null,
                BitDepth=image.Depth is >0 and <=128?(int)image.Depth:null,HasAlpha=image.HasAlpha,SourceColorSpace=Clean(image.ColorSpace.ToString()),
                Capture=CaptureTimeInfo.FromExif(Text(ExifTag.DateTimeOriginal),Text(ExifTag.OffsetTimeOriginal),Text(ExifTag.SubsecTimeOriginal)),ColorState="ready"
            };
            try
            {
                var icc=image.GetColorProfile();if(icc is null)return details with{HasIccProfile=false};
                byte[] data=icc.ToByteArray();if(data.Length>4*1024*1024)return details with{HasIccProfile=true,ColorState="failed",ColorErrorCode="IccProfileBudgetExceeded"};
                return details with{HasIccProfile=true,IccDescription=Clean(icc.Description),IccBytes=data.Length,IccSha256=Convert.ToHexString(SHA256.HashData(data))};
            }
            catch(MagickException){return details with{HasIccProfile=true,ColorState="failed",ColorErrorCode="InvalidIccProfile"};}
        }
        catch(MagickException){return basic with{State="failed",ErrorCode="MetadataReadFailed",Capture=new(){State="failed",ErrorCode="MetadataReadFailed"},ColorState="failed",ColorErrorCode="MetadataReadFailed"};}
        catch(MetadataExtractor.ImageProcessingException){return basic with{State="failed",ErrorCode="MetadataReadFailed",Capture=new(){State="failed",ErrorCode="MetadataReadFailed"},ColorState="failed",ColorErrorCode="MetadataReadFailed"};}
        catch(InvalidDataException){return basic with{State="failed",ErrorCode="MetadataReadBudget",Capture=new(){State="failed",ErrorCode="MetadataReadBudget"},ColorState="failed",ColorErrorCode="MetadataReadBudget"};}
        catch(ArgumentException){return basic with{State="failed",ErrorCode="InvalidMetadata",Capture=new(){State="failed",ErrorCode="InvalidMetadata"},ColorState="failed",ColorErrorCode="InvalidMetadata"};}
        finally{source.Position=position;}
    }
    private static string? Clean(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;string text=new(value.Where(c=>!char.IsControl(c)).Take(512).ToArray());return text.Trim() is {Length:>0} trimmed?trimmed:null;
    }
}
