using System.Diagnostics;
using System.Text;
using NetVips;
using VImage=NetVips.Image;

namespace FolderLens.Media.Worker;

internal sealed class AnimationDecoder : IDisposable
{
    private readonly string path,format;
    private readonly int targetWidth,targetHeight;
    private FileStream? stream;
    private VImage? image;
    private int height,count,index,apngReadIndex;
    private int[] delays=[];
    private Process? process;
    private Task<string>? logTask;
    private int outputWidth,outputHeight;
    private byte[]? frameBuffer;
    public int Count=>count;
    public long TotalPlays {get;private set;}
    public long CompletedLoops {get;private set;}
    public bool Completed=>TotalPlays>0&&CompletedLoops>=TotalPlays;

    public AnimationDecoder(string path,int width,int height,string format,int startFrame=0,long completedLoops=0)
    {
        if(width is <1 or >16384||height is <1 or >16384||completedLoops<0)throw new ArgumentOutOfRangeException(nameof(width));
        this.path=path;this.format=format;targetWidth=width;targetHeight=height;
        try
        {
            if(format=="apng"){var timing=ApngTiming.Read(path);count=timing.Delays.Length;delays=timing.Delays;TotalPlays=timing.TotalPlays;OpenApng();}
            else OpenVips();
            if(startFrame<0||startFrame>=count||TotalPlays>0&&completedLoops>=TotalPlays)throw new ArgumentOutOfRangeException(nameof(startFrame));
            index=startFrame;CompletedLoops=completedLoops;
        }
        catch{Dispose();throw;}
    }
    private void OpenVips()
    {
        image?.Dispose();stream?.Dispose();stream=new(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        image=VImage.NewFromStream(stream,access:Enums.Access.Sequential,kwargs:new VOption{{"n",-1}});
        count=image.GetTypeOf("n-pages")!=0?(int)image.Get("n-pages"):1;height=image.GetTypeOf("page-height")!=0?(int)image.Get("page-height"):image.Height;
        if(count is <1 or >100_000)throw new InvalidDataException("Animation frame metadata budget.");
        delays=image.GetTypeOf("delay")!=0?(int[])image.Get("delay"):[];
        TotalPlays=image.GetTypeOf("loop")!=0?Math.Max(0,Convert.ToInt64(image.Get("loop"))):1;index=0;
    }
    private void OpenApng()
    {
        CloseProcess();
        using var source=VImage.NewFromFile(path);double scale=Math.Min(1,Math.Min((double)targetWidth/source.Width,(double)targetHeight/source.Height));outputWidth=Math.Max(1,(int)(source.Width*scale));outputHeight=Math.Max(1,(int)(source.Height*scale));
        if(checked((long)outputWidth*outputHeight*4)>128*1024*1024)throw new InvalidDataException("Animation frame exceeds memory budget.");
        frameBuffer=new byte[checked(outputWidth*outputHeight*4)];apngReadIndex=0;
        var start=new ProcessStartInfo(AnimationFrameRenderer.FfmpegExecutable()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string argument in new[]{"-nostdin","-v","error","-protocol_whitelist","file,pipe","-f","apng","-ignore_loop","1","-threads",Math.Max(1,NetVips.NetVips.Concurrency-2).ToString(System.Globalization.CultureInfo.InvariantCulture),"-i",path,"-an","-vf",$"scale={outputWidth}:{outputHeight}","-threads","1","-filter_threads","1","-fps_mode","passthrough","-pix_fmt","rgba","-f","rawvideo","pipe:1"})start.ArgumentList.Add(argument);
        process=Process.Start(start)??throw new IOException("APNG decoder unavailable.");
        var activeProcess=process;
        logTask=Task.Run(async()=>{var text=new StringBuilder();char[] buffer=new char[2048];int read;while((read=await activeProcess.StandardError.ReadAsync(buffer))>0)if(text.Length<16384)text.Append(buffer,0,Math.Min(read,16384-text.Length));return text.ToString();});
    }
    public async Task<(int Frame,int Duration,bool End)> Next(string destination,CancellationToken cancellation)
    {
        if(Completed)throw new InvalidOperationException("Animation playback completed.");
        if(index>=count){index=0;if(format=="apng")OpenApng();else OpenVips();}
        if(format=="apng")
        {
            while(apngReadIndex<=index){await process!.StandardOutput.BaseStream.ReadExactlyAsync(frameBuffer!,cancellation);apngReadIndex++;}
            using var frame=VImage.NewFromMemory(frameBuffer!,outputWidth,outputHeight,4,Enums.BandFormat.Uchar);using var rgba=frame.Copy(interpretation:Enums.Interpretation.Srgb);rgba.WriteToFile(destination);
        }
        else
        {
            using var page=image!.Crop(0,checked(index*height),image.Width,height);using var display=StaticDecoder.PrepareDisplay(page);
            double factor=Math.Min(1,Math.Min((double)targetWidth/display.Width,(double)targetHeight/display.Height));
            using var small=StaticDecoder.ResizeForDisplay(display,factor);using var srgb=small.Colourspace(Enums.Interpretation.Srgb);srgb.WriteToFile(destination);
        }
        int current=index++;bool end=index==count;if(end)CompletedLoops=checked(CompletedLoops+1);
        return(current,delays.Length>current?Math.Max(1,delays[current]):100,end);
    }
    private void CloseProcess()
    {
        if(process is null)return;
        try{if(!process.HasExited)process.Kill(entireProcessTree:true);process.WaitForExit();logTask?.GetAwaiter().GetResult();}
        finally{process.Dispose();process=null;logTask=null;frameBuffer=null;}
    }
    public void Dispose(){image?.Dispose();image=null;stream?.Dispose();stream=null;CloseProcess();}
}
