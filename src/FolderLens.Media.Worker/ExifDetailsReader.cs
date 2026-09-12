using System.Globalization;
using System.Security.Cryptography;
using FolderLens.Contracts;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MDirectory=MetadataExtractor.Directory;

namespace FolderLens.Media.Worker;

internal static class ExifDetailsReader
{
    public static ContentMetadataDetails Read(Stream source,string format,ContentMetadataDetails basic)
    {
        source.Position=0;using var bounded=new MetadataReadStream(source);
        var directories=ImageMetadataReader.ReadMetadata(bounded,"."+format);
        var exif=directories.OfType<ExifDirectoryBase>().OrderByDescending(d=>d is ExifSubIfdDirectory).ToArray();
        MDirectory? With(int tag)=>exif.FirstOrDefault(d=>d.ContainsTag(tag));
        string? Text(int tag)=>Clean(With(tag)?.GetString(tag));
        double? Number(MDirectory? directory,int tag)
        {
            if(directory is null||!directory.ContainsTag(tag))return null;
            object? value=directory.GetObject(tag);if(value is Array array)value=array.Length>0?array.GetValue(0):null;
            double result=value switch{Rational rational=>rational.ToDouble(),null=>double.NaN,_=>double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)?parsed:double.NaN};return double.IsFinite(result)?result:null;
        }
        double? N(int tag)=>Number(With(tag),tag);
        int? PositiveInt(double? number)=>number is >0 and <=int.MaxValue?(int)number.Value:null;
        var rawPlane=exif.Where(d=>Number(d,ExifDirectoryBase.TagImageWidth)>0&&Number(d,ExifDirectoryBase.TagImageHeight)>0).OrderByDescending(d=>Number(d,ExifDirectoryBase.TagImageWidth)*Number(d,ExifDirectoryBase.TagImageHeight)).FirstOrDefault();
        var jpeg=directories.OfType<JpegDirectory>().FirstOrDefault();var png=directories.OfType<PngDirectory>().FirstOrDefault(d=>d.ContainsTag(PngDirectory.TagImageWidth));
        int? width=PositiveInt(Number(jpeg,JpegDirectory.TagImageWidth)??Number(png,PngDirectory.TagImageWidth)??Number(rawPlane,ExifDirectoryBase.TagImageWidth)??N(ExifDirectoryBase.TagExifImageWidth));
        int? height=PositiveInt(Number(jpeg,JpegDirectory.TagImageHeight)??Number(png,PngDirectory.TagImageHeight)??Number(rawPlane,ExifDirectoryBase.TagImageHeight)??N(ExifDirectoryBase.TagExifImageHeight));
        int? bits=PositiveInt(Number(jpeg,JpegDirectory.TagDataPrecision)??Number(png,PngDirectory.TagBitsPerSample)??Number(rawPlane,ExifDirectoryBase.TagBitsPerSample));
        double? exposure=N(ExifDirectoryBase.TagExposureTime),aperture=N(ExifDirectoryBase.TagFNumber),focal=N(ExifDirectoryBase.TagFocalLength),iso=N(ExifDirectoryBase.TagIsoSpeed)??N(ExifDirectoryBase.TagIsoEquivalent);
        var icc=directories.OfType<IccDirectory>().FirstOrDefault();byte[]? iccData=With(ExifDirectoryBase.TagInterColorProfile)?.GetByteArray(ExifDirectoryBase.TagInterColorProfile);
        int? iccBytes=iccData?.Length??PositiveInt(Number(icc,IccDirectory.TagProfileByteCount));
        bool badIcc=icc?.HasError==true||iccBytes>4*1024*1024;
        double? color=Number(png,PngDirectory.TagColorType);
        bool? alpha=png is not null?color is 4 or 6||directories.OfType<PngDirectory>().Any(d=>d.ContainsTag(PngDirectory.TagPaletteHasTransparency)):jpeg is not null?false:null;
        var details=basic with
        {
            State="ready",ErrorCode=directories.Any(d=>d.HasError)?"SomeMetadataUnreadable":null,Provider=basic.Provider+"; MetadataExtractor 2.9.3",
            CameraMake=Text(ExifDirectoryBase.TagMake),CameraModel=Text(ExifDirectoryBase.TagModel),LensMake=Text(ExifDirectoryBase.TagLensMake),LensModel=Text(ExifDirectoryBase.TagLensModel)??Text(ExifDirectoryBase.TagLens),
            Iso=iso is >0 and <=long.MaxValue?(long)iso.Value:null,ExposureSeconds=exposure>0?exposure:null,ExposureRational=exposure>0?Text(ExifDirectoryBase.TagExposureTime):null,Aperture=aperture>0?aperture:null,ExposureBias=N(ExifDirectoryBase.TagExposureBias),FocalLengthMm=focal>0?focal:null,FocalLength35Mm=PositiveInt(N(ExifDirectoryBase.Tag35MMFilmEquivFocalLength)),
            EncodedWidth=width,EncodedHeight=height,BitDepth=bits<=128?bits:null,ExifOrientation=N(ExifDirectoryBase.TagOrientation) is {} orientation&&orientation is >=1 and <=8?(int)orientation:null,HasAlpha=alpha,
            Capture=CaptureTimeInfo.FromExif(Text(ExifDirectoryBase.TagDateTimeOriginal),Text(ExifDirectoryBase.TagTimeZoneOriginal),Text(ExifDirectoryBase.TagSubsecondTimeOriginal)),
            HasIccProfile=icc is not null||iccData is not null,IccDescription=Clean(icc?.GetDescription(IccDirectory.TagTagDesc)),IccBytes=iccBytes,IccSha256=iccData is {Length:<=4194304}?Convert.ToHexString(SHA256.HashData(iccData)):null,
            SourceColorSpace=Clean(icc?.GetString(IccDirectory.TagColorSpace)??With(ExifDirectoryBase.TagColorSpace)?.GetDescription(ExifDirectoryBase.TagColorSpace)),ColorState=badIcc?"failed":"ready",ColorErrorCode=badIcc?"InvalidIccProfile":null
        };
        return details;
    }
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:new string(value.Where(c=>!char.IsControl(c)).Take(512).ToArray()).Trim();
    private sealed class MetadataReadStream(Stream source):Stream
    {
        private long bytes;
        private readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
        private void Budget(int count){bytes=checked(bytes+count);if(bytes>32L*1024*1024)throw new InvalidDataException("MetadataReadBudget");if(clock.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("MetadataReadTimeout");}
        public override int Read(byte[] buffer,int offset,int count){Budget(count);return source.Read(buffer,offset,count);}
        public override int Read(Span<byte> buffer){Budget(buffer.Length);return source.Read(buffer);}
        public override int ReadByte(){Budget(1);return source.ReadByte();}
        public override bool CanRead=>true;public override bool CanSeek=>source.CanSeek;public override bool CanWrite=>false;public override long Length=>source.Length;public override long Position{get=>source.Position;set=>source.Position=value;}
        public override long Seek(long offset,SeekOrigin origin)=>source.Seek(offset,origin);public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
