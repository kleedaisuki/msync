using System.Runtime.InteropServices;

namespace Msync.Core.Hpir;

public static partial class HpirArtifact
{
    private static void WriteText(BinaryWriter writer, string text)
    {
        writer.Write(text.Length);
        if (BitConverter.IsLittleEndian) writer.Write(MemoryMarshal.AsBytes(text.AsSpan()));
        else foreach (char code in text) writer.Write((ushort)code);
    }

    private static string ReadText(ImageReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || reader.Bounded && (long)length * sizeof(char) > reader.Remaining)
            throw new MsyncException("HPIR text length exceeds remaining data");
        if (length == 0) return "";
        if (reader.Bounded) return string.Create(length, reader, static (span, input) => ReadTextInto(input, span));

        Span<char> buffer = stackalloc char[256];
        var text = new System.Text.StringBuilder();
        while (length != 0)
        {
            int count = Math.Min(length, buffer.Length);
            ReadTextInto(reader, buffer[..count]);
            text.Append(buffer[..count]);
            length -= count;
        }
        return text.ToString();
    }

    private static void ReadTextInto(ImageReader reader, Span<char> text)
    {
        if (BitConverter.IsLittleEndian) reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(text));
        else foreach (ref char code in text) code = (char)reader.ReadUInt16();
    }
}
