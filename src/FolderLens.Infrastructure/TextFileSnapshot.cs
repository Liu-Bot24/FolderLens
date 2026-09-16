using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Infrastructure;

public sealed record TextFileSnapshot(long Length,long LastWriteTicks,long? ChangeTicks,string? Identity)
{
    public string? SourceSignature {get;init;}
    internal static TextFileSnapshot Capture(FileStream stream)
    {
        bool basicOk=GetBasic(stream.SafeFileHandle,0,out BasicInfo basic,Marshal.SizeOf<BasicInfo>());
        string? identity=GetId(stream.SafeFileHandle,18,out IdInfo id,Marshal.SizeOf<IdInfo>())?$"{id.Volume:X16}:{id.Low:X16}:{id.High:X16}:{basic.Creation:X16}":null;
        if(!basicOk)throw new IOException("无法核验文本文件版本。");
        return new(stream.Length,basic.Write,basic.Change>0?basic.Change:null,identity){SourceSignature=FolderLens.Contracts.FileReadObservation.Read(stream.SafeFileHandle).Signature};
    }
    internal void Validate(string path,FileStream openStream)
    {
        if(Capture(openStream)!=this)throw new IOException("文件已变化，请重新加载。");
        using var current=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1,FileOptions.RandomAccess);
        if(Capture(current)!=this)throw new IOException("文件已替换或变化，请重新加载。");
    }
    public string Key(string path,string encoding,long fileVersion=0)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Path.GetFullPath(path)}\0{encoding}\0{fileVersion}\0{Length}\0{LastWriteTicks}\0{ChangeTicks}\0{Identity}")));
    [StructLayout(LayoutKind.Sequential)]private struct BasicInfo{public long Creation,Access,Write,Change;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct IdInfo{public ulong Volume,Low,High;}
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetBasic(SafeFileHandle handle,int kind,out BasicInfo info,int size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetId(SafeFileHandle handle,int kind,out IdInfo info,int size);
}
