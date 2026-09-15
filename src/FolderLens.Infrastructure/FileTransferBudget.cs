namespace FolderLens.Infrastructure;

/// <summary>Windows StorageItems payloads are eager. Bound the operation before
/// loading rows, and bound retained UTF-16 paths plus per-item interop overhead.</summary>
public sealed class FileTransferBudget
{
    public const int MaximumItems=10_000;
    public const long MaximumBytes=32L<<20;
    private long retainedBytes;
    public FileTransferBudget(long count)
    {
        if(count<0||count>MaximumItems)throw new IOException($"一次最多处理 {MaximumItems:N0} 个文件，请分批选择，或在资源管理器中操作整个文件夹。");
    }
    public void Add(string path)
    {
        retainedBytes=checked(retainedBytes+2048+2L*path.Length);
        if(retainedBytes>MaximumBytes)throw new IOException("所选文件路径过多或过长，请减少选择后重试。");
    }
}
