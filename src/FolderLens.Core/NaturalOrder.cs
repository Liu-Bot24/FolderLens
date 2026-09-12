using System.Buffers.Binary;
using System.Text;

namespace FolderLens.Core;

public static class NaturalOrder
{
    public const int Version = 1;
    // Binary lexicographic key. Numeric runs use significant digit count, never integer parsing.
    public static byte[] Key(string value)
    {
        using var bytes = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        for (int i = 0; i < value.Length;)
        {
            if (value[i] is >= '0' and <= '9')
            {
                int end = i;
                while (end < value.Length && value[end] is >= '0' and <= '9') end++;
                int significant = i;
                while (significant < end - 1 && value[significant] == '0') significant++;
                bytes.WriteByte(1);
                BinaryPrimitives.WriteInt32BigEndian(number, end - significant);
                bytes.Write(number);
                for (int j = significant; j < end; j++) bytes.WriteByte((byte)value[j]);
                i = end;
            }
            else
            {
                bytes.WriteByte(2);
                char c = char.ToUpperInvariant(value[i++]);
                bytes.WriteByte((byte)(c >> 8)); bytes.WriteByte((byte)c);
            }
        }
        bytes.WriteByte(0);
        // Exact original UTF-16 is the deterministic tie breaker (file02 versus file2).
        bytes.Write(Encoding.BigEndianUnicode.GetBytes(value));
        return bytes.ToArray();
    }

    public static int Compare(string a, string b) => Key(a).AsSpan().SequenceCompareTo(Key(b));
}
