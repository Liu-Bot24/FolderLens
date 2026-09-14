using NetVips;
using VImage=NetVips.Image;
namespace FolderLens.Media.Worker;

internal static class RawPreviewImage
{
    public static VImage Decode(byte[] data,RawDecoder.Pixels info,int flip)
    {
        // libraw_processed_image_t distinguishes encoded JPEG/JPEG-XL from RGB pixels.
        if(info.Type is 1 or 3)return VImage.NewFromBuffer(data);
        if(info.Type!=2||info.Width==0||info.Height==0||info.Channels is not (1 or 3)||info.Bits is not (8 or 16)||
            (ulong)info.Width*info.Height>300_000_000||
            (ulong)info.Width*info.Height*info.Channels*(info.Bits/8)!=(ulong)data.Length)
            throw new InvalidDataException("Invalid RAW embedded bitmap.");
        using var memory=VImage.NewFromMemory(data,(int)info.Width,(int)info.Height,(int)info.Channels,info.Bits==8?Enums.BandFormat.Uchar:Enums.BandFormat.Ushort);
        using var owned=memory.CopyMemory();
        using var image=owned.Copy(interpretation:info.Channels==1?(info.Bits==8?Enums.Interpretation.Bw:Enums.Interpretation.Grey16):(info.Bits==8?Enums.Interpretation.Srgb:Enums.Interpretation.Rgb16));
        return image.Mutate(mutable=>mutable.Set(GValue.GIntType,"orientation",flip switch{1=>2,2=>4,3=>3,4=>5,5=>8,6=>6,7=>7,_=>1}));
    }
    public static object Verify()
    {
        byte[] pixels=[255,0,0,0,255,0];var info=new RawDecoder.Pixels{Type=2,Width=2,Height=1,Channels=3,Bits=8};
        using var bitmap=Decode(pixels,info,0);
        if(!bitmap.WriteToMemory<byte>().SequenceEqual(pixels))throw new InvalidDataException("Embedded bitmap pixels changed.");
        using var rotated=Decode(pixels,info,6);using var oriented=rotated.Autorot();
        if(oriented.Width!=1||oriented.Height!=2)throw new InvalidDataException("Embedded bitmap orientation was lost.");
        using var jpeg=Decode(bitmap.JpegsaveBuffer(),new(){Type=1},0);
        if(jpeg.Width!=2||jpeg.Height!=1)throw new InvalidDataException("Embedded JPEG did not decode.");
        bool rejected=false;try{using var invalid=Decode([0],info,0);}catch(InvalidDataException){rejected=true;}
        if(!rejected)throw new InvalidDataException("Truncated bitmap was accepted.");
        return new{status="PASS",bitmap=true,jpeg=true,orientation=true,truncatedRejected=true};
    }
}
