using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderLens.Media.Worker;
using ImageMagick;
using VImage=NetVips.Image;

NetVips.NetVips.Concurrency=4;
NetVips.Cache.Max=0;
NetVips.Cache.MaxMem=64*1024*1024;
var magickConfiguration=ImageMagick.Configuration.ConfigurationFiles.Default;
magickConfiguration.Policy.Data=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"magick","policy.xml"));
string configurationDirectory=Path.Combine(args.Length==5 && args[0]=="serve"?args[2]:Path.Combine(Path.GetTempPath(),"FolderLens-worker",Guid.NewGuid().ToString("N")),"magick");
Directory.CreateDirectory(configurationDirectory);
MagickNET.Initialize(magickConfiguration,configurationDirectory);
if(args.Length==0){Console.WriteLine(JsonSerializer.Serialize(new { vips = NetVips.NetVips.Version(0), magick = MagickNET.Version }));return 0;}
try
{
 if(args[0]=="serve" && args.Length==5){await WorkerServer.Run(args[1],args[2],args[3],args[4]);return 0;}
 if(args[0]=="inspect-image"&&args.Length==3)
 {
    string input=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
    if(output.Equals(Path.GetDirectoryName(input),StringComparison.OrdinalIgnoreCase)||output.StartsWith(Path.GetDirectoryName(input)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Diagnostic output must be outside the source directory.");
    Directory.CreateDirectory(output);using var decoded=new StaticDecoder(input);
    Console.WriteLine(JsonSerializer.Serialize(new{decoded.Width,decoded.Height,decoded.Format,decoded.Provider}));
    decoded.Render(Path.Combine(output,"thumbnail.png"),256,256);decoded.Render(Path.Combine(output,"fit.png"),1920,1080);
    return 0;
 }
 if(args[0]=="image-variants"&&args.Length==2)return ImageVariantProbe.Run(Path.GetFullPath(args[1]));
 if(args[0]=="display-pixels"&&args.Length==2)return DisplayPixelProbe.Run(Path.GetFullPath(args[1]));
 if(args[0]=="generate-health"&&args.Length==2){CapabilityProbe.GenerateFixtures(Path.GetFullPath(args[1]));return 0;}
 if(args[0]=="capabilities"&&args.Length==2){string directory=Path.GetFullPath(args[1]);Directory.CreateDirectory(directory);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var report=await CapabilityProbe.Read(directory,deadline.Token);string json=JsonSerializer.Serialize(report,FolderLens.Contracts.WorkerProtocol.Json);File.WriteAllText(Path.Combine(directory,"capabilities.json"),json);Console.WriteLine(json);return report.Formats.Any(f=>f.BasicDecodeStatus=="FAIL")?1:0;}
 if(args[0]=="raw-smoke" && args.Length==3)
 {
    string path=Path.GetFullPath(args[1]),directory=Path.GetFullPath(args[2]);Directory.CreateDirectory(directory);
    var clock=Stopwatch.StartNew();
    int error=RawDecoder.Open(path,2048,out var handle,out var info);if(error!=0)throw new InvalidDataException($"LibRaw open failed {error}");
    using(handle)
    {
      var embedded=RawDecoder.Decode(handle,false);using var embeddedImage=VImage.NewFromBuffer(embedded.Data);double embeddedMs=clock.Elapsed.TotalMilliseconds;
      string embeddedFile=Path.Combine(directory,"raw-embedded.jpg");File.WriteAllBytes(embeddedFile,embedded.Data);
      clock.Restart();var developed=RawDecoder.Decode(handle,true);double developMs=clock.Elapsed.TotalMilliseconds;
      if(developed.Info.Width<9000 || developed.Info.Height<6000 || developed.Info.Bits!=16)throw new InvalidDataException("Not a full-resolution 16-bit Sony A7R IV A result.");
      using var memory=VImage.NewFromMemory(developed.Data,(int)developed.Info.Width,(int)developed.Info.Height,(int)developed.Info.Channels,NetVips.Enums.BandFormat.Ushort);
      using var rgb16=memory.Copy(interpretation:NetVips.Enums.Interpretation.Rgb16);
      using var small=rgb16.Resize(1600.0/developed.Info.Width,kernel:NetVips.Enums.Kernel.Lanczos3);
      using var srgb=small.Colourspace(NetVips.Enums.Interpretation.Srgb);
      srgb.WriteToFile(Path.Combine(directory,"raw-developed-fit.png"));
      using var crop=rgb16.Crop(4000,2500,1024,1024);using var tile=crop.Colourspace(NetVips.Enums.Interpretation.Srgb);tile.WriteToFile(Path.Combine(directory,"raw-developed-tile.png"));
      var result=new{abi=RawDecoder.Abi(),provider="LibRaw 0.22.2",fixtureId="sony-a7r4a-14bit",embedded=new{quality="rawEmbedded",width=embeddedImage.Width,height=embeddedImage.Height,embedded.Info.Bytes,elapsedMs=embeddedMs},developed=new{quality="rawDeveloped",width=developed.Info.Width,height=developed.Info.Height,bits=developed.Info.Bits,developed.Info.Bytes,elapsedMs=developMs,sha256=Convert.ToHexString(SHA256.HashData(developed.Data))},peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64,sampleCount=1,performanceAcceptance="NOT_RUN: needs 30 samples and actual UI presentation",colorAcceptance="NOT_RUN: independent reference required"};
      string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"raw-result.json"),json);Console.WriteLine(json);return 0;
    }
 }
 if(args[0]=="formats-smoke" && args.Length==3)
 {
    string directory=Path.GetFullPath(args[1]);Directory.CreateDirectory(directory);var results=new List<object>();
    foreach(var format in new[]{MagickFormat.Jpeg,MagickFormat.Png,MagickFormat.Gif,MagickFormat.WebP,MagickFormat.Bmp,MagickFormat.Tiff,MagickFormat.Ico,MagickFormat.Heic,MagickFormat.Avif,MagickFormat.Jxl})
    {
      string file=Path.Combine(directory,"synthetic."+format.ToString().ToLowerInvariant());
      try
      {
        if(format==MagickFormat.Heic)file=Path.GetFullPath(args[2]);
        else {using var fixture=new MagickImage(MagickColors.CornflowerBlue,64,48);fixture.Format=format;fixture.Write(file);}
        using var decoded=new StaticDecoder(file);string output=Path.Combine(directory,format+"-fit.png");decoded.Render(output,128,128);
        using var verify=VImage.NewFromFile(output);byte[] data=verify.WriteToMemory<byte>();
        if((format!=MagickFormat.Heic && (decoded.Width!=64 || decoded.Height!=48)) || data.Length==0)throw new InvalidDataException("Decoded dimensions mismatch.");
        results.Add(new{format=format.ToString(),provider=decoded.Provider,status="PASS",fixture=format==MagickFormat.Heic?"libheif-example":"synthetic-static",width=decoded.Width,height=decoded.Height,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))});
      }
      catch(Exception ex){results.Add(new{format=format.ToString(),status="FAIL",error=ex.Message});}
    }
    string json=JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(directory,"formats-static-result.json"),json);Console.WriteLine(json);return results.Any(r=>JsonSerializer.Serialize(r).Contains("\"FAIL\""))?1:0;
 }
 throw new ArgumentException("Supported commands: raw-smoke <ARW> <output>, formats-smoke <output>.");
}
catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
