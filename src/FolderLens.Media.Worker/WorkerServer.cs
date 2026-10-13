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
                bool inspectingSource=false,sending=false;
                ImageOutput? rendered=null;
                async Task SendResponse()
                {
                    // Once a frame starts, any transport error ends the connection.
                    // Never append a failure JSON in the middle of binary pixels.
                    sending=true;
                    if(response.Status=="ok"&&rendered is not null)
                        response=response with{Pixels=rendered.Descriptor,AssetToken=rendered.AssetPath is null?null:request.RequestId,AssetFormat=rendered.AssetFormat};
                    using var sendDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await WorkerProtocol.Write(pipe,response,sendDeadline.Token);
                        if(response.Pixels is not null)await pipe.WriteAsync(rendered!.Bytes!,sendDeadline.Token);
                    }
                    finally{rendered?.ReleasePixels();}
                }
                try
                {
                    if(request.DeadlineUtc is null || request.DeadlineUtc<DateTimeOffset.UtcNow)throw new TimeoutException();
                    if(request.Operation is not ("capabilities" or "probe" or "thumbnail" or "fit" or "fullTile" or "rawEmbedded" or "rawDevelop" or "page" or "animationOpen" or "animationFrame" or "animationClose"))throw new NotSupportedException();
                    var parameters=request.Parameters?.Deserialize<ImageParameters>(WorkerProtocol.Json)??new();
                    if(parameters.PixelOutput&&parameters.CompactThumbnail
                        ||parameters.PixelOutput&&request.Operation is not("fit" or "fullTile" or "rawEmbedded" or "rawDevelop" or "page" or "animationOpen" or "animationFrame")
                        ||parameters.CompactThumbnail&&request.Operation is not("thumbnail" or "rawEmbedded"))throw new InvalidDataException("Invalid image output mode.");
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
                    inspectingSource=true;ApprovedInput.CheckAccess(File.GetAttributes(input.Path),input.AllowCloud);var stat=new FileInfo(input.Path);
                    input=input.Observe(FileReadObservation.Read(input.Path));
                    inspectingSource=false;
                    if(currentToken!=request.FileRef.InputToken||currentSource!=input)
                    {
                        animation?.Dispose();animation=null;decoder?.Dispose();decoder=null;currentSource=null;
                        decoder=new StaticDecoder(input.Path);input.Observe(decoder.Observation);currentToken=request.FileRef.InputToken;currentSource=input;
                    }
                    void CheckSource(){inspectingSource=true;ApprovedInput.CheckAccess(File.GetAttributes(input.Path),input.AllowCloud);decoder!.CheckVersion();input.Observe(FileReadObservation.Read(input.Path));inspectingSource=false;}
                    NetVips.NetVips.Concurrency=(int)budget.CpuThreads;
                    string token=request.RequestId;
                    string outputPath=Path.Combine(taskDirectory,token+".png");
                    if(request.Operation is not("probe" or "animationClose"))
                        rendered=new ImageOutput(outputPath,parameters.PixelOutput,parameters.CompactThumbnail&&(decoder!.Format=="jpeg"||decoder.IsRaw),budget.MaxOutputBytes);
                    if(!decoder!.Animated&&request.Operation is "animationOpen" or "animationFrame")throw new NotSupportedException("Image is not animated.");
                    if(!decoder!.Animated&&parameters.FrameIndex!=0)throw new NotSupportedException("Static image has no requested animation frame.");
                    if(request.Operation=="fullTile"&&decoder.Animated)
                    {
                        using var deadline=new CancellationTokenSource(request.DeadlineUtc.Value-DateTimeOffset.UtcNow);
                        await AnimationFrameRenderer.RenderTile(input.Path,decoder.Format,decoder.Width,decoder.Height,parameters,outputPath,deadline.Token,rendered);
                        CheckSource();
                        response=response with{Status="ok",Quality="full",AssetToken=token,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0,width=decoder.Width,height=decoder.Height,frameIndex=parameters.FrameIndex,isAnimated=true,provider=decoder.Format=="apng"?"FFmpeg 8.1.2":"NetVips 3.2.0"})};
                        await SendResponse();continue;
                    }
                    if(request.Operation=="page"||decoder!.Format=="tiff"&&request.Operation is "fit" or "fullTile")decoder!.SelectPage(parameters.PageIndex);
                    if(request.Operation=="animationClose"){animation?.Dispose();animation=null;response=response with{Status="ok",Metadata=JsonSerializer.SerializeToElement(new{isFinal=true})};await WorkerProtocol.Write(pipe,response);continue;}
                    if(request.Operation=="animationOpen"){animation?.Dispose();animation=null;animation=new(input.Path,parameters.TargetWidth,parameters.TargetHeight,decoder!.Format,parameters.FrameIndex,parameters.CompletedLoops);}
                    if(request.Operation is "animationOpen" or "animationFrame")
                    {
                        if(animation is null){response=response with{Status="failed",ErrorCode="AnimationSessionLost",Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0})};await WorkerProtocol.Write(pipe,response);continue;}
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var frame=await animation.Next(outputPath,timeout.Token,rendered);
                        CheckSource();
                        response=response with{Status="ok",Quality="fit",AssetToken=token,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=frame.Frame,width=decoder.Width,height=decoder.Height,durationMs=frame.Duration,frameIndex=frame.Frame,endOfLoop=frame.End,frameCount=animation.Count,totalPlays=animation.TotalPlays,completedLoops=animation.CompletedLoops,completed=animation.Completed,isAnimated=true,animationSessionToken=currentToken})};await SendResponse();continue;
                    }
                    var actual=request.Operation=="probe"?(decoder!.Width,decoder.Height,"fit"):decoder!.Render(outputPath,parameters.TargetWidth,parameters.TargetHeight,request.Operation=="fullTile",parameters.TileX,parameters.TileY,request.Operation=="rawEmbedded",thumbnail:request.Operation=="thumbnail",outputTarget:rendered);
                    CheckSource();
                    response=response with{Status="ok",Quality=actual.Item3,AssetToken=request.Operation=="probe"?null:token,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0,width=actual.Item1,height=actual.Item2,targetWidth=parameters.TargetWidth,targetHeight=parameters.TargetHeight,format=decoder.Format,isRaw=decoder.IsRaw,isAnimated=decoder.Animated,pages=decoder.Pages,provider=decoder.Provider,details=request.Operation=="probe"&&parameters.ReadDetails?decoder.ReadDetails():null},WorkerProtocol.Json)};
                    CheckSource();
                }
                catch(Exception ex) when(!sending)
                {
                    string error=ex switch
                    {
                        UnauthorizedAccessException=>"AccessDenied",IOException {Message:"FileChanged"}=>"FileChanged",
                        IOException {Message:"CloudReadNotApproved"}=>"CloudReadNotApproved",
                        _ when decoder?.SourceFailure is not null=>"SourceIoError",
                        TimeoutException=>"Timeout",NotSupportedException=>"UnsupportedCodec",
                        FileNotFoundException or DirectoryNotFoundException when inspectingSource=>"SourceMissing",
                        IOException {Message:"SourceIoError"}=>"SourceIoError",IOException when inspectingSource=>"SourceIoError",_=>"DecodeFailed"
                    };
                    // A native lazy decoder can retain a failed input state.
                    // A retry must reopen that source, not reuse the failed graph.
                    if(error is "SourceIoError" or "SourceMissing")
                    {animation?.Dispose();animation=null;decoder?.Dispose();decoder=null;currentSource=null;currentToken=null;}
                    response=response with{Status=ex is TimeoutException?"timeout":ex is NotSupportedException?"unsupported":error=="FileChanged"?"stale":"failed",ErrorCode=error,AssetToken=null,AssetFormat=null,Pixels=null,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,sequence=0,errorType=ex.GetType().FullName,detail=ex.Message[..Math.Min(ex.Message.Length,1024)]})};
                }
                await SendResponse();
            }
        }
        finally{animation?.Dispose();decoder?.Dispose();}
    }
}
