using System.Buffers.Binary;

namespace FolderLens.Media.Worker;

internal sealed record ApngTiming(long TotalPlays,int[] Delays)
{
    public static ApngTiming Read(string path)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        Span<byte> bytes=stackalloc byte[26];input.ReadExactly(bytes[..8]);
        if(!bytes[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("Invalid PNG signature.");
        int expected=0,chunks=0;long plays=0;var delays=new List<int>();bool ended=false;
        while(input.Position+12<=input.Length&&++chunks<=1_000_000)
        {
            input.ReadExactly(bytes[..8]);uint size=BinaryPrimitives.ReadUInt32BigEndian(bytes);long end=checked(input.Position+size+4);
            if(end>input.Length)throw new InvalidDataException("Truncated APNG chunk.");
            if(bytes.Slice(4,4).SequenceEqual("acTL"u8))
            {
                if(size!=8||expected!=0)throw new InvalidDataException("Invalid APNG control.");input.ReadExactly(bytes[..8]);
                uint count=BinaryPrimitives.ReadUInt32BigEndian(bytes);if(count is 0 or >100_000)throw new InvalidDataException("APNG frame metadata budget.");
                expected=(int)count;plays=BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
            }
            else if(bytes.Slice(4,4).SequenceEqual("fcTL"u8))
            {
                if(size!=26||expected==0||delays.Count>=expected)throw new InvalidDataException("Invalid APNG frame control.");input.ReadExactly(bytes);
                int numerator=BinaryPrimitives.ReadUInt16BigEndian(bytes[20..]),denominator=BinaryPrimitives.ReadUInt16BigEndian(bytes[22..]);
                delays.Add(Math.Max(1,(int)Math.Ceiling(1000.0*numerator/(denominator==0?100:denominator))));
            }
            else if(bytes.Slice(4,4).SequenceEqual("IEND"u8)){ended=true;break;}
            input.Position=end;
        }
        if(!ended||expected==0||delays.Count!=expected)throw new InvalidDataException("Incomplete APNG timing.");
        return new(plays,delays.ToArray());
    }
}
