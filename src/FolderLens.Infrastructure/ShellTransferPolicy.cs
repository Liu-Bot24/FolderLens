namespace FolderLens.Infrastructure;

public static class ShellTransferPolicy
{
    private static readonly SemaphoreSlim volumeGate=new(1,1);
    public static async Task<ShellFileAction> ChooseAsync(IReadOnlyList<string> sources,string destination,bool control,bool shift,string? executable,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(control&&shift)throw new NotSupportedException("此处不支持创建快捷方式。");
        if(control)return ShellFileAction.Copy;if(shift)return ShellFileAction.Move;
        await volumeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
        await using var worker=new ScanWorkerClient(executable??ScanWorkerClient.FindExecutable()??throw new FileNotFoundException("缺少文件访问组件。"));
        string? volume=(await worker.Probe(destination,cancellation).ConfigureAwait(false)).VolumeIdentity;
        if(volume is null||sources.Count==0)return ShellFileAction.Copy;
        foreach(string source in sources)
            if((await worker.Probe(source,cancellation).ConfigureAwait(false)).VolumeIdentity!=volume)return ShellFileAction.Copy;
        return ShellFileAction.Move;
        }
        finally{volumeGate.Release();}
    }
    public static ShellFileAction Choose(IReadOnlyList<string> sources,string destination,bool control,bool shift)
    {
        if(control&&shift)throw new NotSupportedException("此处不支持创建快捷方式。");
        if(control)return ShellFileAction.Copy;if(shift)return ShellFileAction.Move;
        string? volume=FileAllocation.InspectMetadata(destination).VolumeIdentity;
        return volume is not null&&sources.Count>0&&sources.All(p=>FileAllocation.InspectMetadata(p).VolumeIdentity==volume)?ShellFileAction.Move:ShellFileAction.Copy;
    }
}
