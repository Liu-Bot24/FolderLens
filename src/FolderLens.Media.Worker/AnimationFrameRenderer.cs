using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FolderLens.Contracts;
using NetVips;
using VImage=NetVips.Image;

namespace FolderLens.Media.Worker;

internal static class AnimationFrameRenderer
{
    public static string FfmpegExecutable()
    {
        string adjacent=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","native","ffmpeg","ffmpeg.exe"));
        return File.Exists(adjacent)?adjacent:Path.Combine(AppContext.BaseDirectory,"ffmpeg.exe");
    }

    public static async Task RenderTile(string path,string format,int width,int height,ImageParameters parameters,string destination,CancellationToken token)
    {
        if(parameters.FrameIndex<0||parameters.PageIndex!=0||parameters.TileX<0||parameters.TileY<0)throw new ArgumentOutOfRangeException(nameof(parameters));
        int x=checked(parameters.TileX*1024),y=checked(parameters.TileY*1024);
        if(x>=width||y>=height)throw new ArgumentOutOfRangeException(nameof(parameters));
        int tileWidth=Math.Min(1024,width-x),tileHeight=Math.Min(1024,height-y);
        if(format!="apng")
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var header=VImage.NewFromStream(stream);
            int count=header.GetTypeOf("n-pages")!=0?(int)header.Get("n-pages"):1;
            if(parameters.FrameIndex>=count)throw new ArgumentOutOfRangeException(nameof(parameters.FrameIndex));
            using var frameStream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            // The native loader supplies the composited canvas, including previous disposal/blend.
            using var decoded=VImage.NewFromStream(frameStream,access:Enums.Access.Random,kwargs:new VOption{{"page",parameters.FrameIndex},{"n",1}});
            using var display=StaticDecoder.PrepareDisplay(decoded);
            if(display.Width!=width||display.Height!=height)throw new InvalidDataException("Animation canvas changed.");
            using var tile=display.Crop(x,y,tileWidth,tileHeight);using var srgb=tile.Colourspace(Enums.Interpretation.Srgb);srgb.WriteToFile(destination);return;
        }
        if(parameters.FrameIndex>=ReadApngFrameCount(path))throw new ArgumentOutOfRangeException(nameof(parameters.FrameIndex));
        var start=new ProcessStartInfo(FfmpegExecutable()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string argument in new[]{"-nostdin","-v","error","-protocol_whitelist","file,pipe","-f","apng","-ignore_loop","1","-i",path,"-an","-vf",$"select=eq(n\\,{parameters.FrameIndex}),crop={tileWidth}:{tileHeight}:{x}:{y}","-frames:v","1","-threads","2","-filter_threads","1","-pix_fmt","rgba","-f","image2","-y",destination})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("APNG decoder unavailable.");
        var errors=Task.Run(async()=>{var result=new StringBuilder();char[] buffer=new char[2048];int count;while((count=await process.StandardError.ReadAsync(buffer))>0)if(result.Length<16384)result.Append(buffer,0,Math.Min(count,16384-result.Length));return result.ToString();});
        try
        {
            await process.WaitForExitAsync(token);string detail=await errors;
            if(process.ExitCode!=0||!File.Exists(destination)||new FileInfo(destination).Length==0)throw new InvalidDataException("APNG current frame decode failed: "+detail);
        }
        finally
        {
            if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}
            await errors;
        }
    }

    internal static uint ReadApngFrameCount(string path)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);input.Position=8;
        Span<byte> chunk=stackalloc byte[8];int chunks=0;
        while(input.Position+12<=input.Length&&++chunks<=4096)
        {
            input.ReadExactly(chunk);uint size=BinaryPrimitives.ReadUInt32BigEndian(chunk);
            if(size>input.Length-input.Position-4)throw new InvalidDataException("Truncated APNG chunk.");
            if(chunk[4..].SequenceEqual("acTL"u8)){if(size!=8)throw new InvalidDataException("Invalid APNG control.");input.ReadExactly(chunk);uint frames=BinaryPrimitives.ReadUInt32BigEndian(chunk);if(frames==0)throw new InvalidDataException("Empty APNG.");return frames;}
            if(chunk[4..].SequenceEqual("IDAT"u8)||chunk[4..].SequenceEqual("IEND"u8))break;
            input.Position=checked(input.Position+size+4);
        }
        throw new InvalidDataException("Missing APNG animation control.");
    }
}
