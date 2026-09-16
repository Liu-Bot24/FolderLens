using System.Runtime.InteropServices.WindowsRuntime;
using FolderLens.Contracts;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed record MarkdownResource(string Root,string Document,string RelativeUrl,bool Video,RequestContext Context,long Selection,CancellationToken Token)
    {public string? Path;public SourceFileStamp? Stamp;public int Failures;public long RetryAfter;public bool PermanentFailure;}
    private readonly Dictionary<string,MarkdownResource> markdownResources=[];
    private readonly SemaphoreSlim markdownResourceGate=new(2,2);
    private int markdownResourceRequests;
    private long markdownImageBytes;
    private bool markdownViewportBusy;
    private Func<string,CancellationToken,Task>? verifyMarkdownResourceBarrier;
    // Host-owned code only. Markdown scripts, messages, host objects and network access remain disabled.
    private const string MarkdownViewportScript="""
        ((failures)=>{
          for(const node of document.querySelectorAll('img[data-src],video[data-src]')){
            const rect=node.getBoundingClientRect();const visible=rect.bottom>=-160&&rect.top<=innerHeight+160;
            if(node.tagName==='IMG'&&node.complete&&node.naturalWidth>0){node.width=node.naturalWidth;node.height=node.naturalHeight;node.dataset.sized='true';}
            const failure=failures[node.dataset.src];
            if(visible&&node.tagName==='IMG'&&node.hasAttribute('src')&&node.complete&&node.naturalWidth===0&&failure&&!failure.permanent&&Date.now()>=failure.after&&Number(node.dataset.retry||0)<failure.attempt){node.dataset.retry=String(failure.attempt);node.removeAttribute('src');}
            if(visible&&!node.hasAttribute('src')&&(!failure||!failure.permanent&&Date.now()>=failure.after))node.src=node.dataset.src;
            if(!visible&&node.tagName==='VIDEO')node.pause();
            if(node.tagName==='IMG'&&node.dataset.sized&&(rect.bottom < -innerHeight||rect.top > innerHeight*2))node.removeAttribute('src');
            if(node.tagName==='VIDEO'&&!node.paused&&window.folderLensPlayingVideo!==node){window.folderLensPlayingVideo?.pause();window.folderLensPlayingVideo=node;}
          }
        })
        """;
    private void ResetMarkdownResources(){markdownResources.Clear();markdownImages.Clear();markdownImageBytes=0;}
    private async Task UpdateMarkdownViewport()
    {
        // Assigning image src before the document's load event makes navigation
        // completion wait for decoding and defeats body-first rendering.
        if(markdownLoading||markdownViewportBusy||closing||MarkdownHost.Visibility!=Microsoft.UI.Xaml.Visibility.Visible||markdown?.CoreWebView2 is not {} core)return;
        markdownViewportBusy=true;
        try
        {
            var failures=markdownResources.Where(pair=>pair.Value.Failures>0).ToDictionary(pair=>"https://folderlens.local/assets/"+pair.Key,pair=>new{attempt=pair.Value.Failures,after=pair.Value.RetryAfter,permanent=pair.Value.PermanentFailure});
            await core.ExecuteScriptAsync(MarkdownViewportScript+"("+System.Text.Json.JsonSerializer.Serialize(failures)+");");
        }
        catch(Exception error){if(!closing)RecordWebView("MarkdownViewportFailure "+error.GetType().Name);}
        finally{markdownViewportBusy=false;}
    }
    private async Task PauseMarkdownMedia()
    {
        if(markdown?.CoreWebView2 is not {} core)return;
        try{await core.ExecuteScriptAsync("document.querySelectorAll('video').forEach(v=>v.pause())");}
        catch(Exception error){if(!closing)RecordWebView("MarkdownPauseFailure "+error.GetType().Name);}
    }
    private async Task ServeMarkdownResource(WebView2 view,CoreWebView2Environment environment,CoreWebView2WebResourceRequestedEventArgs e)
    {
        using var deferral=e.GetDeferral();bool slot=false,counted=false;long requestedSelection=selection;string requestedDocument=markdownDocumentUrl;MarkdownResource? requestedResource=null;
        void Failed(bool retryable)
        {
            if(requestedResource is not {} resource||resource.Token.IsCancellationRequested||closing||resource.Selection!=selection||requestedDocument!=markdownDocumentUrl)return;
            resource.Failures++;resource.PermanentFailure=!retryable||resource.Failures>3;
            resource.RetryAfter=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000L*(1L<<Math.Min(resource.Failures-1,3));
        }
        CoreWebView2WebResourceResponse Response(byte[] bytes,int status,string reason,string headers)=>environment.CreateWebResourceResponse(new MemoryStream(bytes,false).AsRandomAccessStream(),status,reason,headers+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        try
        {
            e.Response=Response([],403,"Forbidden","Content-Type: text/plain");
            if(!ReferenceEquals(markdown,view)||closing)return;
            if(e.ResourceContext==CoreWebView2WebResourceContext.Document&&e.Request.Uri==markdownDocumentUrl)
            {e.Response=Response(markdownDocument,200,"OK","Content-Type: text/html; charset=utf-8");return;}
            if(!Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="folderlens.local"||!uri.AbsolutePath.StartsWith("/assets/",StringComparison.Ordinal))return;
            string key=uri.Segments.Last();
            if(!markdownResources.TryGetValue(key,out var resource)||resource.Selection!=selection||resource.Token.IsCancellationRequested)return;
            requestedResource=resource;
            using var operation=browserWork.Enter();if(operation is null)return;
            if(markdownResourceRequests>=16){Failed(true);e.Response=Response([],429,"Too Many Requests","");return;}
            markdownResourceRequests++;counted=true;
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(resource.Token,lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(15));var token=timeout.Token;
            await markdownResourceGate.WaitAsync(token);slot=true;
            bool Current()
            {
                if(closing||resource.Token.IsCancellationRequested||!ReferenceEquals(markdown,view)||resource.Selection!=selection||
                    !markdownResources.TryGetValue(key,out var current)||!ReferenceEquals(current,resource))return false;
                // A current document's expired request must enter the retryable
                // failure path even when the preceding await returned normally.
                token.ThrowIfCancellationRequested();return true;
            }
            if(!Current())return;
            if(verifyMarkdownResourceBarrier is {} barrier)await barrier(resource.RelativeUrl,token);
            if(!Current())return;
            if(!resource.Video&&markdownImages.TryGetValue(key,out var cached)){e.Response=Response(cached,200,"OK","Content-Type: image/png");return;}
            string path=await prefetchSourceProbe.ResolveImage(resource.Root,resource.Document,resource.RelativeUrl,token);
            var stamp=await prefetchSourceProbe.Read(path,token);if(!Current())return;
            if(resource.Stamp is {} prior&&(prior!=stamp||resource.Path!=path))throw new InvalidDataException("Markdown 媒体已发生变化。");
            resource.Path=path;resource.Stamp=stamp;
            if(resource.Video)
            {
                string? range=e.Request.Headers.Contains("Range")?e.Request.Headers.GetHeader("Range"):null;
                var part=LocalMediaRange.Parse(range,stamp.Length);
                var reply=await contentWorker!.ReadMediaRange(path,range,resource.Context,stamp,token);if(!Current())return;
                var data=reply.Message.Metadata!.Value;byte[] bytes=data.GetProperty("bytes").GetBytesFromBase64();
                if(bytes.Length!=part.Count||data.GetProperty("start").GetInt64()!=part.Start||data.GetProperty("length").GetInt64()!=stamp.Length)throw new InvalidDataException("Invalid media range response.");
                e.Response=Response(bytes,206,"Partial Content",$"Content-Type: {LocalMediaRange.ContentType(path)}\r\nAccept-Ranges: bytes\r\nContent-Length: {bytes.Length}\r\nContent-Range: bytes {part.Start}-{part.Start+bytes.Length-1}/{stamp.Length}");
                return;
            }
            await thumbnailSlots.WaitAsync(token);var worker=thumbnailPool.Dequeue();ImageReply? image=null;
            try
            {
                image=await worker.Request(path,"thumbnail",resource.Context,new(1024,1024),token,stamp);
                if(!Current())return;
                if(await prefetchSourceProbe.Read(path,token)!=stamp)throw new InvalidDataException("Markdown 图片已发生变化。");
                byte[] bytes=await File.ReadAllBytesAsync(image.AssetPath!,token);if(!Current())return;
                if(bytes.Length>8*1024*1024)throw new InvalidDataException("MarkdownImageLimit");
                if(markdownImages.Remove(key,out var previous))markdownImageBytes-=previous.Length;
                while(markdownImages.Count>0&&(markdownImageBytes+bytes.Length>32L*1024*1024||markdownImages.Count>=32))
                {var oldest=markdownImages.First();markdownImageBytes-=oldest.Value.Length;markdownImages.Remove(oldest.Key);}
                markdownImages[key]=bytes;markdownImageBytes+=bytes.Length;
                e.Response=Response(bytes,200,"OK","Content-Type: image/png");
            }
            finally{try{if(image is not null)await worker.ReleaseAsset(image);}finally{thumbnailPool.Enqueue(worker);thumbnailSlots.Release();}}
        }
        catch(OperationCanceledException){Failed(true);}
        catch(Exception error)
        {
            Failed(error is TimeoutException||error is IOException and not (FileNotFoundException or DirectoryNotFoundException));
            RecordWebView("MarkdownResourceFailure "+error.GetType().Name);
            if(!closing&&requestedSelection==selection&&requestedDocument==markdownDocumentUrl&&ReferenceEquals(markdown,view))Status.Text="部分 Markdown 媒体无法显示；文档正文仍可阅读。";
        }
        finally{if(slot)markdownResourceGate.Release();if(counted)markdownResourceRequests--;}
    }
}
