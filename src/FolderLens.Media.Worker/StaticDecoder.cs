using ImageMagick;
using System.Buffers.Binary;
using NetVips;
using FolderLens.Contracts;
using VImage=NetVips.Image;

namespace FolderLens.Media.Worker;

internal sealed class StaticDecoder : IDisposable
{
    private readonly FileStream source;
    private VImage? image;
    private RawDecoder.Handle? raw;
    private byte[]? rawPixels;
    private readonly string path;
    private readonly long length,mtime;
    private int rawWidth,rawHeight;
    private int rawFlip;
    private int rawEncodedWidth,rawEncodedHeight;
    private int? encodedWidth,encodedHeight,sourceDepth;
    private bool? sourceHasAlpha;
    private int pageIndex;
    public string Provider {get;private set;}="NetVips 3.2.0";
    public string Format {get;private set;}="";
    public bool IsRaw {get;}
    public int Width=>image?.Width??rawWidth;
    public int Height=>image?.Height??rawHeight;
    public int Pages {get;private set;}=1;
    public bool Animated {get;private set;}
    private ContentMetadataDetails? details;
    public ContentMetadataDetails ReadDetails()
    {
        if(details is not null)return details;
        int? frames=Animated?Pages:1,pageCount=Animated?1:Pages,loop=null;
        if(image is not null&&image.GetTypeOf("loop")!=0)loop=Convert.ToInt32(image.Get("loop"));
        if(Animated&&Format=="apng"&&Pages<=1)frames=null;
        details=ImageDetailsReader.Read(source,Format,Provider,frames,pageCount,loop);
        // A RAW container's first TIFF IFD can be its embedded JPEG preview. Its
        // dimensions and 8-bit depth must not be reported as the sensor image.
        if(IsRaw)
        {
            bool sensorPlane=details.EncodedWidth>=rawEncodedWidth&&details.EncodedHeight>=rawEncodedHeight;
            details=details with{EncodedWidth=sensorPlane?details.EncodedWidth:rawEncodedWidth,EncodedHeight=sensorPlane?details.EncodedHeight:rawEncodedHeight,BitDepth=sensorPlane?details.BitDepth:null,SourceColorSpace="camera raw"};
        }
        else details=details with{EncodedWidth=details.EncodedWidth??encodedWidth,EncodedHeight=details.EncodedHeight??encodedHeight,BitDepth=details.BitDepth??sourceDepth,HasAlpha=details.HasAlpha??sourceHasAlpha};
        CheckVersion();return details;
    }

    public StaticDecoder(string file)
    {
        path=file;source=new(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,64*1024,FileOptions.RandomAccess);
        length=source.Length;mtime=File.GetLastWriteTimeUtc(file).Ticks;
        string ext=Path.GetExtension(file).ToLowerInvariant();
        IsRaw=new[]{".arw",".dng",".cr2",".cr3",".nef",".raf",".rw2",".orf",".pef"}.Contains(ext);
        try
        {
            if(IsRaw)
            {
                int result=RawDecoder.Open(file,2048,out raw,out var info);if(result!=0)throw new InvalidDataException($"LibRaw open error {result}");
                rawFlip=info.Flip;rawWidth=rawEncodedWidth=(int)info.Width;rawHeight=rawEncodedHeight=(int)info.Height;if(info.Flip is 5 or 6)(rawWidth,rawHeight)=(rawHeight,rawWidth);
                Format=ext[1..];Provider="LibRaw 0.22.2";return;
            }
            byte[] head=new byte[32];int read=source.Read(head);source.Position=0;Format=Identify(head.AsSpan(0,read));
            if(Format=="png" && IsApng(source)){Format="apng";Animated=true;}
            if(Format is "bmp" or "ico" or "jxl" or "heic")
            {
                uint selectedFrame=Format=="ico"?SelectIconFrame():0;
                using var magick=new MagickImage(source,new MagickReadSettings{Format=Format switch{"bmp"=>MagickFormat.Bmp,"ico"=>MagickFormat.Ico,"heic"=>MagickFormat.Heic,_=>MagickFormat.Jxl},FrameIndex=selectedFrame,FrameCount=1});
                if((ulong)magick.Width*magick.Height>300_000_000)throw new InvalidDataException("Image pixel budget exceeded.");
                encodedWidth=checked((int)magick.Width);encodedHeight=checked((int)magick.Height);sourceDepth=checked((int)magick.Depth);sourceHasAlpha=magick.HasAlpha;
                magick.AutoOrient();using var buffer=new MemoryStream();magick.Write(buffer,MagickFormat.Png);image=VImage.NewFromBuffer(buffer.ToArray());Provider="Magick.NET 14.17.1";
            }
            else
            {
                image=VImage.NewFromStream(source,access:Enums.Access.Random,failOn:Enums.FailOn.Error);
                if(image.GetTypeOf("n-pages")!=0)Pages=(int)image.Get("n-pages");
                Animated=Format=="apng" || (Format is "gif" or "webp" && Pages>1);
            }
            if(checked((long)Width*Height)>300_000_000)throw new InvalidDataException("Image pixel budget exceeded.");
            encodedWidth??=Width;encodedHeight??=Height;
            var display=PrepareDisplay(image);image.Dispose();image=display;
            CheckVersion();
        }
        catch{Dispose();throw;}
    }
    private static bool IsApng(FileStream input)
    {
        input.Position=8;Span<byte> chunk=stackalloc byte[8];
        try{while(input.Position+8<=input.Length){input.ReadExactly(chunk);uint bytes=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(chunk);if(chunk[4..].SequenceEqual("acTL"u8))return true;if(chunk[4..].SequenceEqual("IDAT"u8)||chunk[4..].SequenceEqual("IEND"u8))return false;long next=checked(input.Position+bytes+4);if(next>input.Length)return false;input.Position=next;}return false;}
        finally{input.Position=0;}
    }
    public void SelectPage(int page)
    {
        if(Format!="tiff" || page<0 || page>=Pages)throw new ArgumentOutOfRangeException(nameof(page));
        if(page==pageIndex)return;
        source.Position=0;using var decoded=VImage.NewFromStream(source,access:Enums.Access.Random,kwargs:new VOption{{"page",page},{"n",1}});
        if(checked((long)decoded.Width*decoded.Height)>300_000_000)throw new InvalidDataException("Image pixel budget exceeded.");
        var display=PrepareDisplay(decoded);image?.Dispose();image=display;pageIndex=page;CheckVersion();
    }
    internal static VImage PrepareDisplay(VImage source)
    {
        var oriented=source.Autorot();
        try{if(oriented.GetTypeOf("icc-profile-data")==0)return oriented;var converted=oriented.IccTransform("srgb",embedded:true,depth:16);oriented.Dispose();return converted;}
        catch{oriented.Dispose();throw;}
    }
    private uint SelectIconFrame()
    {
        source.Position=0;
        try
        {
            Span<byte> header=stackalloc byte[6];source.ReadExactly(header);int count=BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            if(count is <1 or >1024||6L+16L*count>source.Length)throw new InvalidDataException("Invalid icon directory.");
            Span<byte> entry=stackalloc byte[16];long largest=-1;int bestDepth=-1;uint best=0;
            for(uint index=0;index<count;index++)
            {
                source.ReadExactly(entry);long bytes=BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),offset=BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                if(bytes==0||offset<6L+16L*count||offset+bytes>source.Length)continue;
                int width=entry[0]==0?256:entry[0],height=entry[1]==0?256:entry[1],depth=BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);long area=(long)width*height;
                if(area>largest||area==largest&&depth>bestDepth){largest=area;bestDepth=depth;best=index;}
            }
            if(largest<0)throw new InvalidDataException("Icon has no bounded image frame.");return best;
        }
        finally{source.Position=0;}
    }
    internal static string Identify(ReadOnlySpan<byte> data)
    {
        if(data.Length<12)throw new InvalidDataException("Incomplete image header.");
        if(data[0]==255 && data[1]==216)return "jpeg";
        if(data.StartsWith(new byte[]{137,80,78,71,13,10,26,10}))return "png";
        if(data[..3].SequenceEqual("GIF"u8))return "gif";
        if(data[..2].SequenceEqual("BM"u8))return "bmp";
        if(data[..4].SequenceEqual("RIFF"u8) && data.Slice(8,4).SequenceEqual("WEBP"u8))return "webp";
        if(data[..4].SequenceEqual(new byte[]{0,0,1,0}))return "ico";
        if(data[..4].SequenceEqual(new byte[]{73,73,42,0}) || data[..4].SequenceEqual(new byte[]{77,77,0,42}) || data[..4].SequenceEqual(new byte[]{73,73,43,0}))return "tiff";
        if(data[0]==255 && data[1]==10 || data.Slice(4,4).SequenceEqual("JXL "u8))return "jxl";
        if(data.Slice(4,4).SequenceEqual("ftyp"u8))return data.Slice(8,4).SequenceEqual("avif"u8)?"avif":"heic";
        throw new NotSupportedException("Unsupported image container.");
    }
    public (int Width,int Height,string Quality) Render(string destination,int targetWidth,int targetHeight,bool full=false,int tileX=0,int tileY=0,bool embedded=false,bool thumbnail=false,bool fastPreview=false)
    {
        if(targetWidth is <1 or >16384 || targetHeight is <1 or >16384 || tileX<0 || tileY<0)throw new ArgumentOutOfRangeException(nameof(targetWidth));
        CheckVersion();
        if(IsRaw && embedded)
        {
            var preview=RawDecoder.Decode(raw!,false);using var decoded=RawPreviewImage.Decode(preview.Data,preview.Info,rawFlip);using var oriented=decoded.Autorot();
            WriteFit(oriented,destination,targetWidth,targetHeight);return(oriented.Width,oriented.Height,"rawEmbedded");
        }
        if(IsRaw && image is null)
        {
            var decoded=RawDecoder.Decode(raw!,true);rawPixels=decoded.Data;
            using var memory=VImage.NewFromMemory(rawPixels,(int)decoded.Info.Width,(int)decoded.Info.Height,(int)decoded.Info.Channels,Enums.BandFormat.Ushort);
            image=memory.Copy(interpretation:Enums.Interpretation.Rgb16);
        }
        if(full)
        {
            int x=checked(tileX*1024),y=checked(tileY*1024);if(x>=Width || y>=Height)throw new ArgumentOutOfRangeException(nameof(tileX));
            using var tile=image!.Crop(x,y,Math.Min(1024,Width-x),Math.Min(1024,Height-y));using var output=tile.Colourspace(Enums.Interpretation.Srgb);output.WriteToFile(destination);
        }
        else if(thumbnail&&Format=="jpeg")
        {
            // Load and resize together so libvips can use JPEG shrink-on-load.
            // Keep a separate stream: the full-resolution decoder may be used later.
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var small=VImage.ThumbnailStream(input,targetWidth,height:targetHeight,size:Enums.Size.Down,outputProfile:"srgb",failOn:Enums.FailOn.Error);
            using var output=small.Colourspace(Enums.Interpretation.Srgb);output.WriteToFile(destination);
        }
        else WriteFit(image!,destination,targetWidth,targetHeight,fastPreview);
        CheckVersion();return(Width,Height,IsRaw?"rawDeveloped":full?"full":"fit");
    }
    private static void WriteFit(VImage input,string destination,int width,int height,bool fastPreview=false)
    {
        double scale=Math.Min(1,Math.Min((double)width/input.Width,(double)height/input.Height));
        using var small=ResizeForDisplay(input,scale);using var output=small.Colourspace(Enums.Interpretation.Srgb);
        if(fastPreview)output.Pngsave(destination,compression:1,filter:Enums.ForeignPngFilter.None);
        else output.WriteToFile(destination);
    }
    internal byte[] MeasureFitPixels(int width,int height)
    {
        if(image is null||IsRaw)throw new NotSupportedException("Static decoded image required.");
        CheckVersion();double scale=Math.Min(1,Math.Min((double)width/image.Width,(double)height/image.Height));
        using var small=ResizeForDisplay(image,scale);using var output=small.Colourspace(Enums.Interpretation.Srgb);
        using var rgba=output.HasAlpha()?output.Copy():output.Bandjoin(255);
        byte[] pixels=rgba.WriteToMemory<byte>();CheckVersion();return pixels;
    }
    internal static VImage ResizeForDisplay(VImage input,double scale)
    {
        if(scale==1)return input.Copy();
        if(!input.HasAlpha())return input.Resize(scale,kernel:Enums.Kernel.Lanczos3);
        // libvips resize interpolates straight RGB. Premultiply first so hidden
        // colors cannot contaminate visible edges; retain source precision.
        using var premultiplied=input.Premultiply();
        using var resized=premultiplied.Resize(scale,kernel:Enums.Kernel.Lanczos3);
        using var straight=resized.Unpremultiply();
        return straight.Cast(input.Format);
    }
    public void CheckVersion(){if(source.Length!=length || File.GetLastWriteTimeUtc(path).Ticks!=mtime)throw new IOException("FileChanged");}
    public void Dispose(){image?.Dispose();image=null;raw?.Dispose();raw=null;rawPixels=null;source.Dispose();}
}
