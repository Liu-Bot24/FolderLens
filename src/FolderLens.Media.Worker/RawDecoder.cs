using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderLens.Media.Worker;

internal static class RawDecoder
{
    [StructLayout(LayoutKind.Sequential)] internal struct Info {public uint Width,Height,ThumbWidth,ThumbHeight;public int Flip;}
    [StructLayout(LayoutKind.Sequential)] internal struct Pixels {public IntPtr Data;public ulong Bytes;public uint Width,Height,Channels,Bits,Type;}
    internal sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle():base(true){}
        protected override bool ReleaseHandle(){RawDecoder.Close(handle);return true;}
    }
    [DllImport("FolderLens.RawBridge.dll",EntryPoint="fl_raw_open",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Open(string path,uint memoryMb,out Handle handle,out Info info);
    [DllImport("FolderLens.RawBridge.dll",EntryPoint="fl_raw_pixels",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Read(Handle handle,int develop,out Pixels pixels);
    [DllImport("FolderLens.RawBridge.dll",EntryPoint="fl_raw_close",CallingConvention=CallingConvention.Cdecl)]
    private static extern void Close(IntPtr handle);
    [DllImport("FolderLens.RawBridge.dll",EntryPoint="fl_raw_abi",CallingConvention=CallingConvention.Cdecl)]
    internal static extern uint Abi();
    internal static void ThrowIfError(int error,string operation)
    {
        // libraw/libraw_const.h, locked LibRaw 0.22.2: distinguish I/O from
        // LIBRAW_DATA_ERROR (-100008), unsupported files and resource limits.
        if(error==-100009)throw new IOException("SourceIoError");
        if(error!=0)throw new InvalidDataException($"LibRaw {operation} error {error}");
    }
    internal static (byte[] Data,Pixels Info) Decode(Handle handle,bool develop)
    {
        int error=Read(handle,develop?1:0,out var pixels);ThrowIfError(error,"decode");
        if(pixels.Bytes>1024UL*1024*1024 || pixels.Bytes==0)throw new InvalidDataException("RAW output budget exceeded.");
        byte[] bytes=new byte[checked((int)pixels.Bytes)];Marshal.Copy(pixels.Data,bytes,0,bytes.Length);return(bytes,pixels);
    }
}
