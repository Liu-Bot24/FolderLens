using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;

namespace FolderLens.Media.Worker;

internal static class ImageVariantProbe
{
    private sealed record Check(string Name,bool Passed,string Detail);
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<Check>();
        void CheckCase(string name,Action check){try{check();checks.Add(new(name,true,"PASS"));}catch(Exception error){checks.Add(new(name,false,error.Message));}}
        string icon=Path.Combine(directory,"multisize.ico");
        using(var small=ColorImage(16,16,0,0,255))using(var large=ColorImage(256,256,255,0,0))
        {
            byte[][] frames=[small.ToByteArray(MagickFormat.Png),large.ToByteArray(MagickFormat.Png)];
            using var writer=new BinaryWriter(File.Create(icon));writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)2);uint offset=38;
            for(int n=0;n<2;n++){writer.Write((byte)(n==0?16:0));writer.Write((byte)(n==0?16:0));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write((uint)frames[n].Length);writer.Write(offset);offset+=checked((uint)frames[n].Length);}
            foreach(byte[] frame in frames)writer.Write(frame);
        }
        string tiff=Path.Combine(directory,"profile-pages.tiff");double[] expected;
        using(var pages=new MagickImageCollection())
        {
            var first=ColorImage(64,32,80,112,144);first.Depth=16;first.Orientation=OrientationType.RightTop;first.SetProfile(ColorProfiles.AdobeRGB1998);pages.Add(first);
            var second=ColorImage(64,32,160,100,60);second.Depth=16;second.Orientation=OrientationType.TopLeft;second.SetProfile(ColorProfiles.AdobeRGB1998);pages.Add(second);
            using var reference=second.Clone();reference.TransformColorSpace(ColorProfiles.SRGB);expected=Pixel(reference);pages.Write(tiff,MagickFormat.Tiff);
        }
        byte[] iconHash=SHA256.HashData(File.ReadAllBytes(icon)),tiffHash=SHA256.HashData(File.ReadAllBytes(tiff));
        CheckCase("ico-largest-frame",()=>{using var decoder=new StaticDecoder(icon);Require(decoder.Width==256&&decoder.Height==256,$"Expected largest 256x256, got {decoder.Width}x{decoder.Height}");decoder.Render(Path.Combine(directory,"largest-fit.png"),256,256);});
        CheckCase("tiff-page-orientation-color",()=>
        {
            using var decoder=new StaticDecoder(tiff);Require(decoder.Pages==2,"Expected two pages");Require(decoder.Width==32&&decoder.Height==64,$"First-page orientation: {decoder.Width}x{decoder.Height}");
            decoder.SelectPage(1);Require(decoder.Width==64&&decoder.Height==32,"Second-page dimensions changed incorrectly");string output=Path.Combine(directory,"second-page.png");decoder.Render(output,64,32);
            using var actualImage=new MagickImage(output);double[] actual=Pixel(actualImage);Require(actual.Zip(expected).All(p=>Math.Abs(p.First-p.Second)<=3),$"Page ICC mismatch: actual {string.Join(',',actual)}; independent Magick sRGB {string.Join(',',expected)}");
        });
        CheckCase("invalid-tiff-page-preserves-current",()=>
        {
            using var decoder=new StaticDecoder(tiff);bool rejected=false;try{decoder.SelectPage(2);}catch(ArgumentOutOfRangeException){rejected=true;}Require(rejected,"Invalid page accepted");Require(decoder.Width==32&&decoder.Height==64,"Invalid request discarded current image");
        });
        CheckCase("source-unchanged",()=>Require(iconHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(icon)))&&tiffHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(tiff))),"Viewing changed the fixture"));
        string json=JsonSerializer.Serialize(new{scenario="synthetic-multivariant-images",checks,scope="Static native provider; not display ICC or complete format acceptance"},new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"image-variant-result.json"),json);Console.WriteLine(json);return checks.All(c=>c.Passed)?0:1;
    }
    private static double[] Pixel(IMagickImage<ushort> image){using var pixels=image.GetPixels();var color=pixels.GetPixel(0,0).ToColor()!;return[color.R/257.0,color.G/257.0,color.B/257.0];}
    private static MagickImage ColorImage(int width,int height,int red,int green,int blue)
    {
        using var blank=NetVips.Image.Black(width,height,bands:3);using var pixels=blank.NewFromImage(new[]{red,green,blue});using var srgb=pixels.Copy(interpretation:NetVips.Enums.Interpretation.Srgb);return new MagickImage(srgb.WriteToBuffer(".png"));
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
}
