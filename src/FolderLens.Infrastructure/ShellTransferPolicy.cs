namespace FolderLens.Infrastructure;

public static class ShellTransferPolicy
{
    public static ShellFileAction Choose(IReadOnlyList<string> sources,string destination,bool control,bool shift)
    {
        if(control&&shift)throw new NotSupportedException("此处不支持创建快捷方式。");
        if(control)return ShellFileAction.Copy;if(shift)return ShellFileAction.Move;
        string? volume=FileAllocation.InspectMetadata(destination).VolumeIdentity;
        return volume is not null&&sources.Count>0&&sources.All(p=>FileAllocation.InspectMetadata(p).VolumeIdentity==volume)?ShellFileAction.Move:ShellFileAction.Copy;
    }
}
