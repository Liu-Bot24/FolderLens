using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace FolderLens.Infrastructure;

public sealed record ActivationRequest(string? Root,string? File)
{
    public static ActivationRequest Parse(string[] arguments)
    {
        string? Value(string option){int index=Array.IndexOf(arguments,option);return index>=0&&index+1<arguments.Length?arguments[index+1]:null;}
        return new(Value("--root"),Value("--open"));
    }
}

/// <summary>One instance per local data directory. Secondary launches forward only
/// an open request through a current-user pipe; no command lines are executed.</summary>
public sealed class SingleInstanceBroker : IAsyncDisposable
{
    private readonly FileStream ownership;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop=new();
    private readonly Channel<ActivationRequest> pending=Channel.CreateBounded<ActivationRequest>(new BoundedChannelOptions(8){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
    private readonly Task listener;
    private int disposed;
    private SingleInstanceBroker(FileStream ownership,string pipeName){this.ownership=ownership;this.pipeName=pipeName;listener=Listen();}
    public static async Task<SingleInstanceBroker?> Acquire(string directory,ActivationRequest activation,CancellationToken cancellation=default)
    {
        directory=Path.GetFullPath(directory);string name="FolderLens-activation-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        FileStream? owner=null;
        try{owner=await Task.Run(()=>{Directory.CreateDirectory(directory);return new FileStream(Path.Combine(directory,".instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);},cancellation).ConfigureAwait(false);}
        catch(IOException ex) when((ex.HResult&65535)==32)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);byte[] data=JsonSerializer.SerializeToUtf8Bytes(activation);if(data.Length>262144)throw new ArgumentException("打开请求过长。");
            await pipe.WriteAsync(BitConverter.GetBytes(data.Length),timeout.Token).ConfigureAwait(false);await pipe.WriteAsync(data,timeout.Token).ConfigureAwait(false);
            byte[] result=new byte[1];await pipe.ReadExactlyAsync(result,timeout.Token).ConfigureAwait(false);if(result[0]!=1)throw new IOException("运行中的查看器未接受打开请求。");return null;
        }
        return new(owner!,name);
    }
    private async Task Listen()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,4096,4096);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    byte[] header=new byte[4];await pipe.ReadExactlyAsync(header,timeout.Token).ConfigureAwait(false);int length=BitConverter.ToInt32(header);if(length is <2 or >262144)throw new InvalidDataException("Invalid activation size.");
                    byte[] body=new byte[length];await pipe.ReadExactlyAsync(body,timeout.Token).ConfigureAwait(false);var request=JsonSerializer.Deserialize<ActivationRequest>(body)??throw new InvalidDataException();
                    if(request.Root?.Length>32767||request.File?.Length>32767)throw new InvalidDataException();pending.Writer.TryWrite(request);await pipe.WriteAsync(new byte[]{1},timeout.Token).ConfigureAwait(false);
                }
                catch(Exception ex) when(ex is IOException or JsonException or OperationCanceledException){if(stop.IsCancellationRequested)break;}
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally{pending.Writer.TryComplete();}
    }
    public async Task Run(Func<ActivationRequest,Task> activate,CancellationToken cancellation=default)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,cancellation);
        try{await foreach(var request in pending.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))await activate(request).ConfigureAwait(false);}
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
    }
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();try{await listener.ConfigureAwait(false);}finally{ownership.Dispose();stop.Dispose();}}
}
