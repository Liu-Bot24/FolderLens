using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using NetVips;
using VImage=NetVips.Image;

namespace FolderLens.Media.Worker;

internal static class DisplayPixelProbe
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();bool passed=true;
        void Check(string name,Action action){try{action();checks.Add(new{name,status="PASS"});}catch(Exception ex){passed=false;checks.Add(new{name,status="FAIL",error=ex.Message});}}
        foreach(bool highDepth in new[]{false,true})Check(highDepth?"rgba16-icc-shrink":"rgba8-shrink",()=>
        {
            string path=Path.Combine(directory,highDepth?"rgba16.png":"rgba8.png");ushort[] pixels=new ushort[32*32*4];
            for(int y=0;y<32;y++)for(int x=0;x<32;x++){int i=(y*32+x)*4;bool visible=x<16;pixels[i]=(ushort)(visible?(highDepth?42001:65535):0);pixels[i+1]=(ushort)(visible&&highDepth?22003:0);pixels[i+2]=(ushort)(visible?(highDepth?18007:0):65535);pixels[i+3]=(ushort)(visible?(highDepth?32769:65535):0);}
            using(var memory=VImage.NewFromMemory(pixels,32,32,4,Enums.BandFormat.Ushort))using(var rgb=memory.Copy(interpretation:Enums.Interpretation.Rgb16))
            using(var fixture=new MagickImage(rgb.WriteToBuffer(".png"))){fixture.Depth=highDepth?16u:8u;if(highDepth)fixture.SetProfile(ColorProfiles.AdobeRGB1998);fixture.Write(path);}
            string hash=Hash(path);double[] expected;
            using(var reference=new MagickImage(path)){if(highDepth)reference.TransformColorSpace(ColorProfiles.SRGB);expected=Pixel(reference,4,4);}
            using var decoder=new StaticDecoder(path);string fit=Path.Combine(directory,highDepth?"rgba16-fit.png":"rgba8-fit.png");decoder.Render(fit,1,1);
            using(var output=new MagickImage(fit)){double[] actual=Pixel(output,0,0);Require(actual.Take(3).Zip(expected).All(p=>Math.Abs(p.First-p.Second)<=3),$"Hidden RGB leaked: actual={string.Join(',',actual)}, visible reference={string.Join(',',expected)}");Require(Math.Abs(actual[3]-expected[3]/2)<=5,"Alpha coverage changed");}
            string full=Path.Combine(directory,highDepth?"rgba16-full.png":"rgba8-full.png");decoder.Render(full,32,32,full:true);
            using(var output=new MagickImage(full))Require(Pixel(output,4,4).Zip(expected).All(p=>Math.Abs(p.First-p.Second)<=3),"Native-size ICC/alpha mismatch");
            Require(Hash(path)==hash,"Source changed");
        });
        int[][] corners=[[0,1,2,3],[1,0,3,2],[3,2,1,0],[2,3,0,1],[0,2,1,3],[2,0,3,1],[3,1,2,0],[1,3,0,2]];
        byte[][] colors=[[255,0,0],[0,255,0],[0,0,255],[255,255,0]];
        for(int orientation=1;orientation<=8;orientation++)Check($"exif-{orientation}-fit-full",()=>
        {
            byte[] pixels=new byte[80*48*3];for(int y=0;y<48;y++)for(int x=0;x<80;x++)colors[(y>=24?2:0)+(x>=40?1:0)].CopyTo(pixels,(y*80+x)*3);
            string path=Path.Combine(directory,$"orientation-{orientation}.jpg");
            using(var memory=VImage.NewFromMemory(pixels,80,48,3,Enums.BandFormat.Uchar))using(var rgb=memory.Copy(interpretation:Enums.Interpretation.Srgb))
            using(var fixture=new MagickImage(rgb.WriteToBuffer(".png"))){var profile=new ExifProfile();profile.SetValue(ExifTag.Orientation,(ushort)orientation);fixture.SetProfile(profile);fixture.Orientation=(OrientationType)orientation;fixture.Quality=100;fixture.Write(path);}
            using(var fixture=new MagickImage(path))Require((int)fixture.Orientation==orientation,"Fixture orientation did not persist");
            string hash=Hash(path);using var decoder=new StaticDecoder(path);int w=orientation>=5?48:80,h=orientation>=5?80:48;Require(decoder.Width==w&&decoder.Height==h,"Oriented dimensions incorrect");
            foreach(bool full in new[]{false,true}){string output=Path.Combine(directory,$"orientation-{orientation}-{full}.png");decoder.Render(output,w,h,full:full);using var image=new MagickImage(output);for(int c=0;c<4;c++){double[] actual=Pixel(image,c%2==0?8:w-9,c<2?8:h-9);Require(actual.Take(3).Zip(colors[corners[orientation-1][c]]).All(p=>Math.Abs(p.First-p.Second)<=4),$"Corner {c} mismatch");}}
            Require(Hash(path)==hash,"Source changed");
        });
        string json=JsonSerializer.Serialize(new{checks,scope="Native pixel conversion only; display ICC and UI NOT_RUN"},new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"display-pixels.json"),json);Console.WriteLine(json);return passed?0:1;
    }
    private static double[] Pixel(IMagickImage<ushort> image,int x,int y){using var pixels=image.GetPixels();var c=pixels.GetPixel(x,y).ToColor()!;return[c.R/257.0,c.G/257.0,c.B/257.0,c.A/257.0];}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
}
