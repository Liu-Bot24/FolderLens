using System.Runtime.InteropServices;
using System.Text;

namespace FolderLens.Infrastructure;

public sealed record TextEncodingSelection(Encoding Encoding,int BomLength);

/// <summary>Shared source used by the reader and content worker. Only restartable encodings are accepted.</summary>
public static class TextEncodingPolicy
{
    public static TextEncodingSelection Detect(ReadOnlySpan<byte> sample,bool isWholeFile,string? selectedEncoding=null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;int bom=0;
        if(sample.StartsWith(new byte[]{0xEF,0xBB,0xBF})){encoding=new UTF8Encoding(false,true);bom=3;}
        else if(sample.StartsWith(new byte[]{0xFF,0xFE})){encoding=new UnicodeEncoding(false,false,true);bom=2;}
        else if(sample.StartsWith(new byte[]{0xFE,0xFF})){encoding=new UnicodeEncoding(true,false,true);bom=2;}
        else if(selectedEncoding is not null)encoding=Encoding.GetEncoding(selectedEncoding,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
        else encoding=new UTF8Encoding(false,true);
        if(!encoding.IsSingleByte && encoding.CodePage is not (65001 or 1200 or 1201 or 54936 or 936 or 932 or 949 or 950 or 1361))
            throw new InvalidDataException("此编码无法按独立字符边界分页，请选择 UTF-8、UTF-16、GB18030、GBK 或本地代码页。");
        try
        {
            var decoder=encoding.GetDecoder();char[] output=new char[encoding.GetMaxCharCount(sample.Length)];
            decoder.Convert(sample[bom..],output,isWholeFile,out _,out _,out _);
        }
        catch(DecoderFallbackException ex){throw new InvalidDataException("编码无法确定或内容无效，请选择正确编码。",ex);}
        if(bom==0 && selectedEncoding is null && sample.Contains((byte)0))throw new InvalidDataException("该文件可能是二进制文件，请选择正确编码。");
        return new(encoding,bom);
    }

    // Maps decoded UTF-16 indices to ORIGINAL bytes, including noncanonical GB18030 encodings.
    // Re-encoding a string would give incorrect source positions when a code point has alternate byte sequences.
    internal static int[] MapCharacters(ReadOnlySpan<byte> bytes,int characters,Encoding encoding)
    {
        int[] map=new int[checked(characters+1)];int source=0,target=0;
        while(source<bytes.Length)
        {
            int count;
            if(encoding.CodePage==65001)count=bytes[source]<0x80?1:bytes[source]<0xE0?2:bytes[source]<0xF0?3:4;
            else if(encoding.CodePage is 1200 or 1201)
            {
                int unit=encoding.CodePage==1200?bytes[source]|bytes[source+1]<<8:bytes[source]<<8|bytes[source+1];
                count=unit is >=0xD800 and <=0xDBFF?4:2;
            }
            else if(encoding.IsSingleByte)count=1;
            else if(encoding.CodePage==54936)count=bytes[source] is >=0x81 and <=0xFE?(bytes[source+1] is >=0x30 and <=0x39?4:2):1;
            else if(encoding.CodePage==936)count=bytes[source] is >=0x81 and <=0xFE?2:1;
            else count=IsDBCSLeadByteEx((uint)encoding.CodePage,bytes[source])?2:1;
            int units=encoding.CodePage==65001?count==4?2:1:encoding.CodePage is 1200 or 1201?count/2:encoding.GetCharCount(bytes.Slice(source,count));
            for(int n=0;n<units;n++)map[target++]=source;
            source+=count;
        }
        if(target!=characters)throw new InvalidDataException("编码边界与原始字节映射不一致。");
        map[target]=source;return map;
    }
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsDBCSLeadByteEx(uint codePage,byte value);
}
