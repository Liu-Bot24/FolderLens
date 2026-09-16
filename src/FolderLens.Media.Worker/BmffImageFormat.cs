using System.Buffers.Binary;

namespace FolderLens.Media.Worker;

internal static class BmffImageFormat
{
    // The brand table is metadata, not an excuse to buffer the container.
    internal const int MaximumFileTypeBytes=64*1024;
    internal static string Read(Stream source)
    {
        long position=source.Position;
        try
        {
            Span<byte> header=stackalloc byte[16];source.ReadExactly(header[..8]);
            if(!header.Slice(4,4).SequenceEqual("ftyp"u8))throw new NotSupportedException("Unsupported image container.");
            ulong size=BinaryPrimitives.ReadUInt32BigEndian(header);int headerSize=8;
            if(size==1){source.ReadExactly(header[8..]);size=BinaryPrimitives.ReadUInt64BigEndian(header[8..]);headerSize=16;}
            if(size==0)size=checked((ulong)(source.Length-position));
            if(size<(ulong)(headerSize+8)||size>MaximumFileTypeBytes||size>(ulong)(source.Length-position)||(size-(ulong)headerSize-8)%4!=0)
                throw new InvalidDataException("Invalid or oversized image file-type box.");
            byte[] brands=new byte[(int)size-headerSize];source.ReadExactly(brands);
            bool avif=false,heic=false,sequence=false;
            for(int offset=0;offset<brands.Length;offset+=4)
            {
                if(offset==4)continue; // minor version is not a brand
                var brand=brands.AsSpan(offset,4);
                avif|=brand.SequenceEqual("avif"u8);sequence|=brand.SequenceEqual("avis"u8);
                heic|=brand.SequenceEqual("heic"u8)||brand.SequenceEqual("heix"u8)||brand.SequenceEqual("heim"u8)||brand.SequenceEqual("heis"u8);
            }
            if(sequence)throw new NotSupportedException("Animated AVIF is not supported.");
            if(avif)return "avif";
            if(heic)return "heic";
            throw new NotSupportedException("Unsupported image container brands.");
        }
        finally{source.Position=position;}
    }
}
