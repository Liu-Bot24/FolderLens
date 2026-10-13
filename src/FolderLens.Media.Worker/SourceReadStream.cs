namespace FolderLens.Media.Worker;

// Native decoders may translate callback IO exceptions into generic decode
// errors. Retain the actual source failure without guessing from native text.
internal sealed class SourceReadStream : FileStream
{
    private readonly Action<IOException> failed;
    private SourceReadStream(string path,Action<IOException> failed,int bufferSize)
        :base(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,bufferSize,FileOptions.RandomAccess)=>this.failed=failed;
    public static SourceReadStream Open(string path,Action<IOException> failed,int bufferSize=64*1024)
    {
        try{return new(path,failed,bufferSize);}
        catch(IOException error){throw new IOException("SourceIoError",error);}
    }
    public override int Read(byte[] buffer,int offset,int count)
    {try{return base.Read(buffer,offset,count);}catch(IOException error){failed(error);throw;}}
    public override int Read(Span<byte> buffer)
    {try{return base.Read(buffer);}catch(IOException error){failed(error);throw;}}
    public override int ReadByte()
    {try{return base.ReadByte();}catch(IOException error){failed(error);throw;}}
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
    {try{return await base.ReadAsync(buffer,cancellationToken).ConfigureAwait(false);}catch(IOException error){failed(error);throw;}}
    public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken)
    {try{return await base.ReadAsync(buffer,offset,count,cancellationToken).ConfigureAwait(false);}catch(IOException error){failed(error);throw;}}
    public override long Seek(long offset,SeekOrigin origin)
    {try{return base.Seek(offset,origin);}catch(IOException error){failed(error);throw;}}
    public override long Position
    {
        get{try{return base.Position;}catch(IOException error){failed(error);throw;}}
        set{try{base.Position=value;}catch(IOException error){failed(error);throw;}}
    }
    public override long Length
    {get{try{return base.Length;}catch(IOException error){failed(error);throw;}}}
}
