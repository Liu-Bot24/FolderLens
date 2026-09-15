using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Content.Worker;
using FolderLens.Infrastructure;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

if(args.Length!=5 || args[0]!="serve"){Console.Error.WriteLine("Content worker requires a supervised named-pipe session.");return 2;}
string directory=args[2],instance=args[4];
await using var textSession=new ContentTextSession(directory);
using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(10_000);
await WorkerProtocol.Write(pipe,new(){Type="hello",WorkerInstanceId=instance,BuildId=WorkerProtocol.BuildId,Nonce=args[3]});
while(pipe.IsConnected)
{
    WorkerEnvelope request;
    try{request=await WorkerProtocol.Read(pipe);}catch(EndOfStreamException){break;}
    if(request.Type!="request"||request.WorkerInstanceId!=instance||request.FileRef is null||!WorkerProtocol.SafeToken(request.FileRef.InputToken)||request.RequestId is null||!WorkerProtocol.SafeToken(request.RequestId))throw new InvalidDataException("Invalid content request.");
    var response=new WorkerEnvelope{Type="response",WorkerInstanceId=instance,RequestId=request.RequestId,Context=request.Context};
    bool inspectingSource=false;
    try
    {
        if(ContentTextSession.Supports(request.Operation))
        {
            var data=await textSession.Execute(request);
            response=response with{Status="ok",Metadata=JsonSerializer.SerializeToElement(data,WorkerProtocol.Json)};
        }
        else
        {
        if(request.Operation!="markdownRender")throw new NotSupportedException();
        var input=JsonSerializer.Deserialize<ApprovedInput>(await File.ReadAllTextAsync(Path.Combine(directory,request.FileRef.InputToken+".input.json")),WorkerProtocol.Json)??throw new InvalidDataException();
        inspectingSource=true;ApprovedInput.CheckAccess(File.GetAttributes(input.Path),input.AllowCloud);var stat=new FileInfo(input.Path);input=input.Observe(stat.Length,stat.LastWriteTimeUtc.Ticks);inspectingSource=false;
        if(input.Length>8*1024*1024)throw new InvalidDataException("MarkdownInputLimit");
        using var stream=new FileStream(input.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(stream.Length!=input.Length||File.GetLastWriteTimeUtc(input.Path).Ticks!=input.LastWriteTicks)throw new IOException("FileChanged");
        byte[] data=new byte[checked((int)input.Length!.Value)];stream.ReadExactly(data);
        string? selectedEncoding=request.Parameters is {} parameters && parameters.TryGetProperty("encoding",out var encodingValue) && encodingValue.ValueKind==JsonValueKind.String?encodingValue.GetString():null;
        var detected=TextEncodingPolicy.Detect(data,true,selectedEncoding);
        string source=detected.Encoding.GetString(data,detected.BomLength,data.Length-detected.BomLength);
                int lines=1;
        for(int n=0;n<source.Length;n++)
        {
            if(source[n]=='\r'){if(n+1<source.Length && source[n+1]=='\n')n++;lines++;}
            else if(source[n]=='\n')lines++;
            if(lines>50_000)throw new InvalidDataException("MarkdownLineLimit");
        }
        var pipeline=new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().UseAutoLinks().DisableHtml().Build();var document=Markdown.Parse(source,pipeline);
        var resources=new List<object>();int count=0;
        foreach(var node in document.Descendants())
        {
            if(++count>100_000)throw new InvalidDataException("MarkdownAstLimit");
            if(node is not LinkInline link)continue;
            if(link.IsImage)
            {
                string target=link.Url??"";
                if(resources.Count<200 && target.Length<4096 && !Uri.TryCreate(target,UriKind.Absolute,out _) && !target.StartsWith('\\'))
                {
                    string token=Guid.NewGuid().ToString("N");resources.Add(new{token,relativeUrl=target});link.Url="https://folderlens.local/assets/"+token;
                }
                else {link.IsImage=false;link.Url="#";}
            }
            else if(link.Url is {} url && !url.StartsWith('#') && (!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))){link.Url="#";}
        }
        using var writer=new BoundedHtmlWriter(16*1024*1024);var renderer=new HtmlRenderer(writer);pipeline.Setup(renderer);renderer.Render(document);writer.Flush();
        string html="<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; img-src https://folderlens.local;\"><style>body{font:16px/1.65 'Segoe UI',sans-serif;max-width:80ch;margin:24px;color:#222;background:#fff}img{max-width:100%;height:auto}pre{overflow:auto;padding:12px;background:#f0f2f5}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:6px}a{color:#2466b0}</style></head><body>"+writer+"</body></html>";
        if(Encoding.UTF8.GetByteCount(html)>16*1024*1024)throw new InvalidDataException("MarkdownHtmlLimit");
        await File.WriteAllTextAsync(Path.Combine(directory,request.RequestId+".html"),html,new UTF8Encoding(false));
        if(stream.Length!=input.Length||File.GetLastWriteTimeUtc(input.Path).Ticks!=input.LastWriteTicks)throw new IOException("FileChanged");
        response=response with{Status="ok",AssetToken=request.RequestId,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true,resources,encoding=detected.Encoding.WebName})};
        }
    }
    catch(Exception ex)
    {
        string error=ex switch
        {
            IOException io when io.Message=="FileChanged" || io.Message.Contains("文件已变化") || io.Message.Contains("文件已替换")=>"FileChanged",
            OperationCanceledException=>"TextCancelled",
            TimeoutException=>"TextTimeout",
            InvalidDataException=>ex.Message,
            UnauthorizedAccessException=>"TextAccessDenied",
            FileNotFoundException or DirectoryNotFoundException when inspectingSource=>"SourceMissing",
            IOException {Message:"CloudReadNotApproved"}=>"CloudReadNotApproved",
            IOException when ContentTextSession.Supports(request.Operation)=>"TextIoError",
            IOException when inspectingSource=>"SourceIoError",
            _=>ContentTextSession.Supports(request.Operation)?"TextFailed":"MarkdownFailed"
        };
        response=response with{Status="failed",ErrorCode=error,Metadata=JsonSerializer.SerializeToElement(new{isFinal=true})};
    }
    await WorkerProtocol.Write(pipe,response);
}
return 0;

internal sealed class BoundedHtmlWriter(int maximum) : StringWriter
{
    private int used;
    private void Reserve(ReadOnlySpan<char> value){used=checked(used+Encoding.UTF8.GetByteCount(value));if(used>maximum)throw new InvalidDataException("MarkdownHtmlLimit");}
    public override void Write(string? value){Reserve(value);base.Write(value);}
    public override void Write(char value){Span<char> span=stackalloc char[1];span[0]=value;Reserve(span);base.Write(value);}
    public override void Write(char[] buffer,int index,int count){Reserve(buffer.AsSpan(index,count));base.Write(buffer,index,count);}
}
