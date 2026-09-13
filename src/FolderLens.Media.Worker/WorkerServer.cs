using System.IO.Pipes;
using System.Text.Json;
using FolderLens.Contracts;

namespace FolderLens.Media.Worker;

internal static class WorkerServer
{
    public static async Task Run(string pipeName,string taskDirectory,string nonce,string instance)
    {
        ImageMagick.MagickNET.SetTempDirectory(taskDirectory);
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10_000);
        await WorkerProtocol.Write(pipe,new(){Type="hello",WorkerInstanceId=instance,BuildId=WorkerProtocol.BuildId,Nonce=nonce});
        StaticDecoder? decoder=null;AnimationDecoder? animation=null;string? currentToken=null;ApprovedInput? currentSource=null;
        try
        {
            while(pipe.IsConnected)
            {
                WorkerEnvelope request;
                try{request=await WorkerProtocol.Read(pipe);}catch(EndOfStreamException){break;}
                if(request.WorkerInstanceId!=instance)throw new InvalidDataException("Worker instance mismatch.");
                if(request.Type=="cancel")continue;
                if(request.Type!="request" || request.Context is null || request.Operation!="capabilities"&&(request.FileRef is null || !WorkerProtocol.SafeToken(request.FileRef.InputToken)) || request.RequestId is null || !WorkerProtocol.SafeToken(request.RequestId))throw new InvalidDataException("Invalid request.");
                var response=new WorkerEnvelope{Type="response",WorkerInstanceId=instance,RequestId=request.RequestId,Context=request.Context};
                bool inspectingSource=false;
                try
                {
                    if(request.DeadlineUtc is null || request.DeadlineUtc<DateTimeOffset.UtcNow)throw new TimeoutException();
                    if(request.Operation is not ("capabilities" or "probe" or "thumbnail" or "fit" or "fullTile" or "rawEmbedded" or "rawDevelop" or "page" or "animationOpen" or "animationFrame" or "animationClose"))throw new NotSupportedException();
                    var parameters=request.Parameters?.Deserialize<ImageParameters>(WorkerProtocol.Json)??new();
                    if(parameters.Level!=0 || parameters.FrameIndex<0 || parameters.CompletedLoops<0 || parameters.CompletedLoops!=0&&request.Operation!="animationOpen" || parameters.FrameIndex!=0&&request.Operation is not("fullTile" or "animationOpen") || parameters.TileSize!=1024)throw new NotSupportedException("Invalid frame/LOD request.");
                    if(request.ResourceBudget is not {MaxPixels:>0,MemoryBytes:>0,CpuThreads:>0} budget || budget.CpuThreads>16)throw new InvalidDataException("Invalid budget.");
                    NetVips.NetVips.Concurrency=(int)budget.CpuThreads;
                    if(request.Operation=="capabilities")
                    {
                        if(request.FileRef is not null)throw new InvalidDataException("Capabilities must not reference a source file.");
                        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var capabilities=await CapabilityProbe.Read(taskDirectory,deadline.Token);
                        response=response with{Status="ok",Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0,capabilities},WorkerProtocol.Json)};await WorkerProtocol.Write(pipe,response);continue;
                    }
                    string inputFile=Path.Combine(taskDirectory,request.FileRef!.InputToken+".input.json");
                    var input=JsonSerializer.Deserialize<ApprovedInput>(await File.ReadAllTextAsync(inputFile),WorkerProtocol.Json)??throw new InvalidDataException();
                    inspectingSource=true;var stat=new FileInfo(input.Path);
                    input=input.Observe(stat.Length,stat.LastWriteTimeUtc.Ticks);
                    inspectingSource=false;
                    if(currentToken!=request.FileRef.InputToken||currentSource!=input)
                    {
                        animation?.Dispose();animation=null;decoder?.Dispose();decoder=null;currentSource=null;
                        decoder=new StaticDecoder(input.Path);currentToken=request.FileRef.InputToken;currentSource=input;
                    }
                    void CheckSource(){decoder!.CheckVersion();inspectingSource=true;stat.Refresh();input.Observe(stat.Length,stat.LastWriteTimeUtc.Ticks);inspectingSource=false;}
                    NetVips.NetVips.Concurrency=(int)budget.CpuThreads;
                    string token=request.RequestId;
                    if(!decoder!.Animated&&request.Operation is "animationOpen" or "animationFrame")throw new NotSupportedException("Image is not animated.");
                    if(!decoder!.Animated&&parameters.FrameIndex!=0)throw new NotSupportedException("Static image has no requested animation frame.");
                    if(request.Operation=="fullTile"&&decoder.Animated)
                    {
                        using var deadline=new CancellationTokenSource(request.DeadlineUtc.Value-DateTimeOffset.UtcNow);
                        string output=Path.Combine(taskDirectory,token+".png");
                        await AnimationFrameRenderer.RenderTile(input.Path,decoder.Format,decoder.Width,decoder.Height,parameters,output,deadline.Token);
                        CheckSource();if(new FileInfo(output).Length>budget.MaxOutputBytes)throw new InvalidDataException("Output budget exceeded.");
                        response=response with{Status="ok",Quality="full",AssetToken=token,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0,width=decoder.Width,height=decoder.Height,frameIndex=parameters.FrameIndex,isAnimated=true,provider=decoder.Format=="apng"?"FFmpeg 8.0.1":"NetVips 3.2.0"})};
                        await WorkerProtocol.Write(pipe,response);continue;
                    }
                    if(request.Operation=="page"||decoder!.Format=="tiff"&&request.Operation is "fit" or "fullTile")decoder!.SelectPage(parameters.PageIndex);
                    if(request.Operation=="animationClose"){animation?.Dispose();animation=null;response=response with{Status="ok",Metadata=JsonSerializer.SerializeToElement(new{isFinal=true})};await WorkerProtocol.Write(pipe,response);continue;}
                    if(request.Operation=="animationOpen"){animation?.Dispose();animation=null;animation=new(input.Path,parameters.TargetWidth,parameters.TargetHeight,decoder!.Format,parameters.FrameIndex,parameters.CompletedLoops);}
                    if(request.Operation is "animationOpen" or "animationFrame")
                    {
                        if(animation is null){response=response with{Status="failed",ErrorCode="AnimationSessionLost",Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0})};await WorkerProtocol.Write(pipe,response);continue;}
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var frame=await animation.Next(Path.Combine(taskDirectory,token+".png"),timeout.Token);
                        CheckSource();if(new FileInfo(Path.Combine(taskDirectory,token+".png")).Length>budget.MaxOutputBytes)throw new InvalidDataException("Output budget exceeded.");
                        response=response with{Status="ok",Quality="fit",AssetToken=token,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=frame.Frame,width=decoder.Width,height=decoder.Height,durationMs=frame.Duration,frameIndex=frame.Frame,endOfLoop=frame.End,frameCount=animation.Count,totalPlays=animation.TotalPlays,completedLoops=animation.CompletedLoops,completed=animation.Completed,isAnimated=true,animationSessionToken=currentToken})};await WorkerProtocol.Write(pipe,response);continue;
                    }
                    var actual=request.Operation=="probe"?(decoder!.Width,decoder.Height,"fit"):decoder!.Render(Path.Combine(taskDirectory,token+".png"),parameters.TargetWidth,parameters.TargetHeight,request.Operation=="fullTile",parameters.TileX,parameters.TileY,request.Operation=="rawEmbedded",thumbnail:request.Operation=="thumbnail");
                    CheckSource();
                    if(request.Operation!="probe" && new FileInfo(Path.Combine(taskDirectory,token+".png")).Length>budget.MaxOutputBytes)throw new InvalidDataException("Output budget exceeded.");
                    response=response with{Status="ok",Quality=actual.Item3,AssetToken=request.Operation=="probe"?null:token,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0,width=actual.Item1,height=actual.Item2,format=decoder.Format,isRaw=decoder.IsRaw,isAnimated=decoder.Animated,pages=decoder.Pages,provider=decoder.Provider,details=request.Operation=="probe"?decoder.ReadDetails():null},WorkerProtocol.Json)};
                }
                catch(Exception ex)
                {
                    string error=ex switch
                    {
                        TimeoutException=>"Timeout",NotSupportedException=>"UnsupportedCodec",UnauthorizedAccessException=>"AccessDenied",
                        FileNotFoundException or DirectoryNotFoundException when inspectingSource=>"SourceMissing",IOException {Message:"FileChanged"}=>"FileChanged",
                        IOException when inspectingSource=>"SourceIoError",_=>"DecodeFailed"
                    };
                    response=response with{Status=ex is TimeoutException?"timeout":ex is NotSupportedException?"unsupported":error=="FileChanged"?"stale":"failed",ErrorCode=error,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0})};
                }
                await WorkerProtocol.Write(pipe,response);
            }
        }
        finally{animation?.Dispose();decoder?.Dispose();}
    }
}
