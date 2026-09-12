using System.Buffers.Binary;
using System.Text;

namespace FolderLens.UnitTests;

internal static class AnimationTimingFixture
{
    // Only edits test-container timing metadata; production composition uses native providers.
    public static void FiniteCopy(string source,string destination,string format)
    {
        byte[] bytes=File.ReadAllBytes(source);
        if(format=="gif")
        {
            int at=bytes.AsSpan().IndexOf("NETSCAPE2.0"u8);if(at<0)throw new InvalidDataException("Fixture has no loop extension.");
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at+13,2),1); // one repeat plus the initial play
        }
        else if(format=="webp")
        {
            bool found=false;for(int at=12;at+8<=bytes.Length;)
            {
                int length=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+4,4)));
                if(Encoding.ASCII.GetString(bytes,at,4)=="ANIM"){BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at+12,2),2);found=true;break;}
                at=checked(at+8+length+(length&1));
            }
            if(!found)throw new InvalidDataException("Fixture has no WebP loop control.");
        }
        else
        {
            int last=-1;
            for(int at=8;at+12<=bytes.Length;)
            {
                int length=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at,4)));string type=Encoding.ASCII.GetString(bytes,at+4,4);
                if(type=="acTL"){BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at+12,4),2);UpdateCrc(bytes,at,length);}
                if(type=="fcTL")last=at;at=checked(at+12+length);
            }
            if(last<0)throw new InvalidDataException("Fixture has no APNG frame control.");
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(last+28,2),777);BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(last+30,2),1000);UpdateCrc(bytes,last,26);
        }
        File.WriteAllBytes(destination,bytes);
    }
    private static void UpdateCrc(byte[] data,int at,int length)
    {
        uint crc=uint.MaxValue;foreach(byte item in data.AsSpan(at+4,length+4)){crc^=item;for(int bit=0;bit<8;bit++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at+8+length,4),~crc);
    }
}
