using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FolderLens.Core;
using FolderLens.Contracts;

namespace FolderLens.Infrastructure;

public sealed record MediaMetadata(long? DurationMs,int? Width,int? Height,string? VideoCodec,string? AudioCodec,double? FrameRate,bool HasCover,int? VideoStream)
{
    public string? FormatId {get;init;}
    public long? FrameRateNumerator {get;init;}
    public long? FrameRateDenominator {get;init;}
    public ContentMetadataDetails? Details {get;init;}
    public int? CoverStream {get;init;}
}

public static class BoundedProcess
{
    public static Task<string> Run(string executable,IEnumerable<string> arguments,TimeSpan timeout,int maxOutputBytes,CancellationToken cancellation)
        =>Task.Run(()=>RunCore(executable,arguments,timeout,maxOutputBytes,cancellation),cancellation);
    private static async Task<string> RunCore(string executable,IEnumerable<string> arguments,TimeSpan timeout,int maxOutputBytes,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)!};foreach(var arg in arguments)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Unable to start media tool.");using var job=new WorkerJob(1024L*1024*1024);job.Assign(process);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(timeout);
        async Task<string> ReadBounded(StreamReader reader)
        {
            var result=new StringBuilder();char[] buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer.AsMemory(),deadline.Token))>0){if(result.Length+count>maxOutputBytes/2)throw new InvalidDataException("Media output exceeded budget.");result.Append(buffer,0,count);}return result.ToString();
        }
        try
        {
            Task<string> stdout=ReadBounded(process.StandardOutput),stderr=ReadBounded(process.StandardError);
            var reads=Task.WhenAll(stdout,stderr);
            var exited=process.WaitForExitAsync(deadline.Token);
            Task first=await Task.WhenAny(reads,exited);if(first.IsFaulted)await first;
            await Task.WhenAll(reads,exited);
            if(process.ExitCode!=0)throw new InvalidDataException($"媒体工具无法读取此文件（退出码 {process.ExitCode}）。");return stdout.Result;
        }
        catch{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}if(!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)throw new TimeoutException("媒体处理超时。");throw;}
    }
}
public sealed class MediaTools(string ffprobe,string ffmpeg)
{
    public const string CoverStrategyVersion="cover-v2-embedded-first-frame-fallback";
    private const string AllowedFormats="mov,matroska,webm,avi,mpegts,wav,mp3,flac,ogg,aac,asf,gif,apng";
    public async Task<MediaMetadata> Probe(string path,CancellationToken cancellation,WorkerPriority priority=WorkerPriority.Metadata)
    {
        PathRules.ValidateSource(path);
        using var lease=await WorkerResources.Shared.Acquire(priority,cancellation).ConfigureAwait(false);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lease.PressureCancellation);
        string json=await BoundedProcess.Run(ffprobe,["-v","error","-threads","1","-protocol_whitelist","file,pipe","-format_whitelist",AllowedFormats,"-show_streams","-show_format","-of","json",path],TimeSpan.FromSeconds(10),4*1024*1024,stop.Token).ConfigureAwait(false);
        using var document=JsonDocument.Parse(json);var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        bool Cover(JsonElement stream)=>stream.TryGetProperty("disposition",out var d)&&d.TryGetProperty("attached_pic",out var attached)&&attached.GetInt32()==1;
        string? Text(JsonElement element,string name)=>element.TryGetProperty(name,out var value)?value.ToString():null;
        double? Number(JsonElement element,string name)=>double.TryParse(Text(element,name),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)&&value>=0?value:null;
        var videos=streams.Where(s=>Text(s,"codec_type")=="video" && !Cover(s)).OrderByDescending(s=>s.TryGetProperty("disposition",out var d)&&d.TryGetProperty("default",out var def)?def.GetInt32():0).ThenByDescending(s=>(Number(s,"width")??0)*(Number(s,"height")??0)).ToArray();
        var audio=streams.FirstOrDefault(s=>Text(s,"codec_type")=="audio");var video=videos.FirstOrDefault();
        if(video.ValueKind==JsonValueKind.Undefined && audio.ValueKind==JsonValueKind.Undefined)throw new InvalidDataException("文件中没有可识别的音频或视频轨道。");
        int? width=null,height=null,index=null,encodedWidth=null,encodedHeight=null;double? fps=null,rotation=null;long? fpsNum=null,fpsDen=null;string? videoCodec=null,audioCodec=audio.ValueKind==JsonValueKind.Undefined?null:Text(audio,"codec_name");
        if(video.ValueKind!=JsonValueKind.Undefined)
        {
            width=(int?)Number(video,"width");height=(int?)Number(video,"height");encodedWidth=width;encodedHeight=height;index=(int?)Number(video,"index");videoCodec=Text(video,"codec_name");
            string[] sar=(Text(video,"sample_aspect_ratio")??"1:1").Split(':');if(sar.Length==2 && double.TryParse(sar[0],out double num)&&double.TryParse(sar[1],out double den)&&num>0&&den>0&&width is {} w)width=checked((int)Math.Round(w*num/den));
            if(video.TryGetProperty("side_data_list",out var side))foreach(var entry in side.EnumerateArray())if(entry.TryGetProperty("rotation",out var rot)&&rot.TryGetDouble(out double angle)&&double.IsFinite(angle)){rotation=angle;if(Math.Abs(angle)%180 is >45 and <135)(width,height)=(height,width);}
            string[] rate=(Text(video,"avg_frame_rate")??"0/0").Split('/');if(rate.Length==2&&long.TryParse(rate[0],NumberStyles.None,CultureInfo.InvariantCulture,out long numerator)&&long.TryParse(rate[1],NumberStyles.None,CultureInfo.InvariantCulture,out long denominator)&&numerator>0&&denominator>0){fpsNum=numerator;fpsDen=denominator;fps=(double)numerator/denominator;}
        }
        long? duration=document.RootElement.TryGetProperty("format",out var format)&&Number(format,"duration") is {} seconds?checked((long)(seconds*1000)):null;
        string? container=format.ValueKind==JsonValueKind.Undefined?null:Text(format,"format_name")?.Split(',')[0];
        string? Tag(JsonElement element,string name)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty("tags",out var tags)?Text(tags,name):null;
        string? creation=Tag(video,"creation_time")??Tag(format,"creation_time")??Tag(audio,"creation_time");
        string? colorTransfer=video.ValueKind==JsonValueKind.Undefined?null:Text(video,"color_transfer");
        int? channels=audio.ValueKind==JsonValueKind.Undefined?null:(int?)Number(audio,"channels"),sampleRate=audio.ValueKind==JsonValueKind.Undefined?null:(int?)Number(audio,"sample_rate");
        var details=new ContentMetadataDetails
        {
            State="ready",Provider="ffprobe-v1",Capture=CaptureTimeInfo.FromIso(creation,"container/stream.creation_time"),ColorState="unsupported",ColorErrorCode="NotApplicable",
            EncodedWidth=encodedWidth>0?encodedWidth:null,EncodedHeight=encodedHeight>0?encodedHeight:null,RotationDegrees=rotation,
            BitRate=format.ValueKind==JsonValueKind.Undefined?null:(long?)Number(format,"bit_rate"),AudioChannels=channels,SampleRate=sampleRate,
            SampleAspectRatio=video.ValueKind==JsonValueKind.Undefined?null:Text(video,"sample_aspect_ratio"),DisplayAspectRatio=video.ValueKind==JsonValueKind.Undefined?null:Text(video,"display_aspect_ratio"),
            ColorPrimaries=video.ValueKind==JsonValueKind.Undefined?null:Text(video,"color_primaries"),ColorTransfer=colorTransfer,IsHdr=colorTransfer is null or "unknown"?null:colorTransfer is "smpte2084" or "arib-std-b67",
            BitDepth=video.ValueKind==JsonValueKind.Undefined?null:Number(video,"bits_per_raw_sample") is {} depth&&depth is >0 and <=128?(int)depth:null,
            Streams=streams.Take(16).Select(s=>new MediaStreamDetails((int)(Number(s,"index")??0),Text(s,"codec_type")??"unknown",Text(s,"codec_name"),(int?)Number(s,"width"),(int?)Number(s,"height"),(int?)Number(s,"channels"),(int?)Number(s,"sample_rate"),s.TryGetProperty("disposition",out var d)&&d.TryGetProperty("default",out var def)&&def.GetInt32()==1,Cover(s))).ToArray(),StreamsTruncated=streams.Length>16
        };
        int? coverIndex=streams.Where(s=>Text(s,"codec_type")=="video"&&Cover(s)).Select(s=>(int?)Number(s,"index")).FirstOrDefault();
        return new(duration,width>0?width:null,height>0?height:null,videoCodec,audioCodec,fps,coverIndex is not null,index){FormatId=container,FrameRateNumerator=fpsNum,FrameRateDenominator=fpsDen,Details=details,CoverStream=coverIndex};
    }
    public Task Cover(string path,string destination,MediaMetadata metadata,CancellationToken cancellation,int edge=512,WorkerPriority priority=WorkerPriority.Visible)=>Task.Run(()=>CoverCore(path,destination,metadata,cancellation,edge,priority),cancellation);
    private async Task CoverCore(string path,string destination,MediaMetadata metadata,CancellationToken cancellation,int edge,WorkerPriority priority)
    {
        PathRules.ValidateSource(path);
        if(edge is not (512 or 1024))throw new ArgumentOutOfRangeException(nameof(edge));
        destination=Path.GetFullPath(destination);
        if(Path.GetFullPath(path).Equals(destination,StringComparison.OrdinalIgnoreCase)||File.Exists(destination))throw new IOException("封面输出路径已存在，不能覆盖。");
        if(metadata.CoverStream is null&&metadata.VideoStream is null)throw new InvalidDataException("此音频没有内嵌封面。");
        using var lease=await WorkerResources.Shared.Acquire(priority,cancellation,WorkerLane.VideoCover).ConfigureAwait(false);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lease.PressureCancellation);stop.CancelAfter(TimeSpan.FromSeconds(15));
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp.png";
        async Task Extract(int stream,double time)
        {
            var arguments=new List<string>{"-nostdin","-v","error","-protocol_whitelist","file,pipe","-format_whitelist",AllowedFormats,"-threads","1","-filter_threads","1"};
            if(time>0)arguments.AddRange(["-ss",time.ToString(CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-i",path,"-map",$"0:{stream}","-frames:v","1","-vf",$"scale={edge}:{edge}:force_original_aspect_ratio=decrease","-c:v","png","-threads","1","-f","image2","-y",temporary]);
            await BoundedProcess.Run(ffmpeg,arguments,TimeSpan.FromSeconds(15),1024*1024,stop.Token).ConfigureAwait(false);
            if(!File.Exists(temporary))throw new InvalidDataException("没有提取到可解码的视频帧。");
            using var image=ThumbnailCache.OpenOwnedRead(temporary);Span<byte> header=stackalloc byte[24];
            if(image.Length is <24 or >16777216||image.Read(header)!=24||!header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||!header.Slice(12,4).SequenceEqual("IHDR"u8))throw new InvalidDataException("封面输出无效。");
            uint width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16,4)),height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20,4));
            if(width<1||height<1||width>edge||height>edge)throw new InvalidDataException("封面尺寸超过限制。");
        }
        void DiscardTemporary(){if(File.Exists(temporary))ThumbnailCache.DeleteOwned(temporary);}
        try
        {
            double time=metadata.CoverStream is not null?0:metadata.DurationMs is >=2000?Math.Min(30,metadata.DurationMs.Value/10000.0):0;
            int stream=metadata.CoverStream??metadata.VideoStream!.Value;
            try{await Extract(stream,time).ConfigureAwait(false);}
            catch(InvalidDataException) when(metadata.VideoStream is not null&&(metadata.CoverStream is not null||time>0))
            {
                DiscardTemporary();stop.Token.ThrowIfCancellationRequested();await Extract(metadata.VideoStream.Value,0).ConfigureAwait(false);
            }
            stop.Token.ThrowIfCancellationRequested();File.Move(temporary,destination);
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested&&!lease.PressureCancellation.IsCancellationRequested){throw new TimeoutException("媒体封面处理超时。");}
        finally{DiscardTemporary();}
    }
    public sealed record PlaylistBatch(string Path,int Count,long FirstOrdinal,long LastOrdinal);
    public static async Task<PlaylistBatch> Playlist(CatalogStore catalog,ResultHandle handle,string root,string directory,long firstOrdinal,CancellationToken cancellation,IReadOnlyList<OrdinalRange>? selection=null)
    {
        if(firstOrdinal<0 || firstOrdinal>handle.Count)throw new ArgumentOutOfRangeException(nameof(firstOrdinal));
        cancellation.ThrowIfCancellationRequested();
        var ranges=selection is null?(handle.Count==0?Array.Empty<OrdinalRange>():new[]{new OrdinalRange(0,handle.Count)}):OrdinalSelection.Normalize(selection,handle.Count);
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".m3u8");
        string temporary=path+".tmp";int written=0;long first=-1,last=-1;
        try
        {
            await using(var output=new StreamWriter(temporary,false,new UTF8Encoding(false)))
            {
                await output.WriteLineAsync("#EXTM3U".AsMemory(),cancellation);
                await output.WriteLineAsync(PlaylistFiles.OwnershipHeader(DateTimeOffset.UtcNow).AsMemory(),cancellation);
                foreach(var range in ranges)
                {
                    for(long offset=Math.Max(firstOrdinal,range.Start);offset<range.End;offset+=256)
                    {
                        int pageCount=(int)Math.Min(256,range.End-offset);
                        var rows=await catalog.ReadPage(handle.Id,offset,pageCount,cancellation);
                        if(rows.Count!=pageCount)throw new InvalidDataException("播放结果已失效，请重新应用筛选。");
                        foreach(var row in rows)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if(row.Kind!="video")continue;
                            if(written==10_000)throw new InvalidOperationException("一次最多播放 10,000 个视频，请缩小筛选或选择范围。");
                            string basePath=Path.GetFullPath(handle.CollectionId is null?root:row.SourceRootPath??throw new InvalidDataException("收藏快照缺少源目录。")).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
                            string file=Path.GetFullPath(Path.Combine(basePath,row.RelativePath));
                            if(file.Any(char.IsControl)||!file.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("播放列表包含无效或越界路径。");
                            if(first<0)first=row.Ordinal;last=row.Ordinal;written++;
                            await output.WriteLineAsync(file.AsMemory(),cancellation);
                        }
                    }
                }
            }
            if(written==0)throw new InvalidOperationException("该范围没有可播放的视频。");
            cancellation.ThrowIfCancellationRequested();File.Move(temporary,path);
            return new(path,written,first,last);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
