using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using FolderLens.Contracts;

internal static class RetainedMemoryFixture
{
    public static async Task<int> Run(string[] args)
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(lifetime.Token);
        await WorkerProtocol.Write(pipe,new(){Type="hello",WorkerInstanceId=args[4],Nonce=args[3],BuildId=WorkerProtocol.BuildId},lifetime.Token);
        IntPtr retained=IntPtr.Zero;
        try
        {
            while(true)
            {
                var request=await WorkerProtocol.Read(pipe,lifetime.Token);
                if(retained!=IntPtr.Zero){Marshal.FreeHGlobal(retained);retained=IntPtr.Zero;}
                int bytes=Math.Clamp(request.Parameters!.Value.Deserialize<ImageParameters>(WorkerProtocol.Json)!.TargetWidth,1,128)*1024*1024;
                retained=Marshal.AllocHGlobal(bytes);
                for(int offset=0;offset<bytes;offset+=4096)Marshal.WriteByte(retained,offset,1);
                string? asset=request.Operation=="fit"?request.RequestId:null;
                if(asset is not null)await File.WriteAllBytesAsync(Path.Combine(args[2],asset+".png"),[1,2,3],lifetime.Token);
                await WorkerProtocol.Write(pipe,new(){Type="response",WorkerInstanceId=args[4],RequestId=request.RequestId,Context=request.Context,Status="ok",AssetToken=asset,Metadata=JsonSerializer.SerializeToElement(new{pid=Environment.ProcessId,retainedBytes=bytes})},lifetime.Token);
            }
        }
        catch(EndOfStreamException){return 0;}
        finally{if(retained!=IntPtr.Zero)Marshal.FreeHGlobal(retained);}
    }
}
