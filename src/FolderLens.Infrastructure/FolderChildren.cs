namespace FolderLens.Infrastructure;

public static class FolderChildren
{
    public static Task<DirectoryListing> Read(string path,string storage,CancellationToken cancellation)=>Task.Run(async()=>
    {
        string executable=ScanWorkerClient.FindExecutable()??throw new InvalidOperationException("目录读取组件缺失。");
        await using var worker=new ScanWorkerClient(executable);
        using var children=new DirectoryListing.Builder(storage,cancellation);bool complete=false;
        await foreach(var packet in worker.Read(path,false,cancellation).ConfigureAwait(false))
        {
            if(packet.State is "failed" or "excluded")throw new IOException("目录暂时无法展开："+packet.ErrorCode);
            foreach(var entry in packet.Entries.Where(entry=>entry.Directory))
            {
                children.Add(Path.Combine(path,entry.Name));
            }
            complete|=packet.State=="completed";
        }
        if(!complete)throw new IOException("目录读取尚未完成，请再次展开重试。");
        return children.Complete();
    },cancellation);
}
