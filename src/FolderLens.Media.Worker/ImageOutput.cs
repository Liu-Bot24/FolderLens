using FolderLens.Contracts;
using NetVips;
using VImage=NetVips.Image;

namespace FolderLens.Media.Worker;

/// <summary>Separate short-lived display pixels from persistent thumbnail encoding.</summary>
internal sealed class ImageOutput(string destination,bool pixels,bool compactThumbnail,long maximumBytes)
{
    public PixelPayload? Descriptor {get;private set;}
    public byte[]? Bytes {get;private set;}
    public string? AssetFormat {get;private set;}
    public string? AssetPath {get;private set;}
    public void ReleasePixels()=>Bytes=null;

    public void Write(VImage image)
    {
        if(Descriptor is not null||AssetPath is not null)throw new InvalidOperationException("Image output already written.");
        using var srgb=image.Colourspace(Enums.Interpretation.Srgb);
        if(pixels)
        {
            var descriptor=new PixelPayload(srgb.Width,srgb.Height,checked(srgb.Width*4),checked(srgb.Width*srgb.Height*4));
            descriptor.Validate(maximumBytes); // Check before allocating/rasterizing the output.
            using var rgba=srgb.HasAlpha()?srgb.Copy():srgb.Bandjoin(255);
            if(rgba.Bands!=4)throw new InvalidDataException("Expected RGB with one alpha channel.");
            using var premultiplied=rgba.Premultiply(maxAlpha:255);
            using var bytes=premultiplied.Cast(Enums.BandFormat.Uchar);
            var buffer=bytes.WriteToMemory<byte>();
            if(buffer.Length!=descriptor.ByteLength)throw new InvalidDataException("Pixel payload length changed.");
            Descriptor=descriptor;Bytes=buffer;
        }
        else
        {
            // Preserve transparency and line-art losslessly; compact photographic thumbnails.
            AssetFormat=compactThumbnail&&!srgb.HasAlpha()?"jpeg":"png";
            AssetPath=AssetFormat=="jpeg"?Path.ChangeExtension(destination,".jpg"):destination;
            if(AssetFormat=="jpeg")srgb.Jpegsave(AssetPath,q:80,keep:Enums.ForeignKeep.None);
            else srgb.WriteToFile(AssetPath);
            if(new FileInfo(AssetPath).Length>maximumBytes)throw new InvalidDataException("Output budget exceeded.");
        }
    }
}
