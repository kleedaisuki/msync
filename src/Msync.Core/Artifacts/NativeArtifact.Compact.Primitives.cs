using System.Text;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private const int CompactMarker = unchecked((int)0x80004952);
    private const byte CompactRevision = 4;

    private enum CompactSection : byte
    {
        Header = 1, Types, Layouts, Projections, Statics, Constants, Functions, Debug, Code, Allocations, Plans, Backend
    }

    private sealed class CompactPool
    {
        internal readonly List<string> Strings = [];
        internal readonly List<SourceSpan> Spans = [];
        private readonly Dictionary<string, int> strings = new(StringComparer.Ordinal);
        private readonly Dictionary<SourceSpan, int> spans = [];

        internal int String(string value)
        {
            if (strings.TryGetValue(value, out int index)) return index;
            index = Strings.Count;
            strings.Add(value, index); Strings.Add(value);
            return index;
        }

        internal int Source(SourceSpan value)
        {
            if (spans.TryGetValue(value, out int index)) return index;
            index = Spans.Count;
            spans.Add(value, index); Spans.Add(value); String(value.Source);
            return index;
        }
    }

    private sealed class CompactWriter(Stream stream, CompactPool pool) : BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)
    {
        public override void Write(int value) => Unsigned(unchecked((uint)((value << 1) ^ (value >> 31))));
        public override void Write(ushort value) => Unsigned(value);
        internal void Unsigned(uint value) => WriteUnsigned(this, value);
        internal void Text(string value) => Unsigned((uint)pool.String(value));
        internal void Symbol(string? value) => Unsigned(value is null ? 0 : checked((uint)pool.String(value) + 1));
        internal void Span(SourceSpan? value) => Unsigned(value is null ? 0 : checked((uint)pool.Source(value.Value) + 1));
        internal void Immediate(long value) => WriteUnsigned64(this, unchecked((ulong)((value << 1) ^ (value >> 63))));
        internal void Inputs(IReadOnlyList<int>? values)
        {
            Unsigned(values is null ? 0 : checked((uint)values.Count + 1));
            if (values is not null) foreach (int value in values) Write(value);
        }
    }

    private sealed class CompactReader(Stream stream, string[] strings, SourceSpan[] spans) : BinaryReader(stream, Encoding.UTF8, leaveOpen: true)
    {
        internal bool CanPreallocate => BaseStream is SectionStream section && section.KnownAvailable;
        internal long Remaining => BaseStream is SectionStream section ? section.Remaining
            : BaseStream.CanSeek ? BaseStream.Length - BaseStream.Position : long.MaxValue;
        public override int ReadInt32()
        {
            uint encoded = Unsigned();
            return unchecked((int)(encoded >> 1) ^ -(int)(encoded & 1));
        }
        public override ushort ReadUInt16()
        {
            uint value = Unsigned();
            if (value > ushort.MaxValue) throw new MsyncException("Native IR UInt16 operand overflows");
            return (ushort)value;
        }
        internal uint Unsigned() => ReadUnsigned(this);
        internal string Text() => At(strings, Unsigned(), "string");
        internal string? Symbol()
        {
            uint index = Unsigned();
            return index == 0 ? null : At(strings, index - 1, "symbol");
        }
        internal SourceSpan? Span()
        {
            uint index = Unsigned();
            return index == 0 ? null : At(spans, index - 1, "source span");
        }
        internal long Immediate()
        {
            ulong encoded = ReadUnsigned64(this);
            return unchecked((long)(encoded >> 1) ^ -(long)(encoded & 1));
        }
        internal int[]? Inputs()
        {
            uint count = Unsigned();
            if (count == 0) return null;
            if (count - 1 > int.MaxValue || count - 1 > Remaining)
                throw new MsyncException("Native IR operand vector exceeds remaining section bytes");
            if (CanPreallocate)
            {
                int[] result = new int[(int)count - 1];
                for (int index = 0; index < result.Length; index++) result[index] = ReadInt32();
                return result;
            }
            List<int> streamed = [];
            for (uint index = 1; index < count; index++) streamed.Add(ReadInt32());
            return streamed.ToArray();
        }
        private static T At<T>(T[] table, uint index, string name) => index < table.Length
            ? table[(int)index] : throw new MsyncException($"Invalid native IR {name} pool index {index}");
    }

    private static void WriteUnsigned(BinaryWriter writer, uint value)
    {
        while (value >= 0x80) { writer.Write((byte)(value | 0x80)); value >>= 7; }
        writer.Write((byte)value);
    }

    private static uint ReadUnsigned(BinaryReader reader)
    {
        uint value = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte part = reader.ReadByte();
            if (shift == 28 && part > 0x0f) throw new MsyncException("Native IR integer operand overflows");
            value |= (uint)(part & 0x7f) << shift;
            if ((part & 0x80) == 0)
            {
                if (shift != 0 && part == 0) throw new MsyncException("Noncanonical native IR integer operand");
                return value;
            }
        }
        throw new MsyncException("Invalid native IR integer operand");
    }

    private static void WriteUnsigned64(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80) { writer.Write((byte)(value | 0x80)); value >>= 7; }
        writer.Write((byte)value);
    }

    private static ulong ReadUnsigned64(BinaryReader reader)
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            byte part = reader.ReadByte();
            if (shift == 63 && part > 1) throw new MsyncException("Native IR immediate overflows");
            value |= (ulong)(part & 0x7f) << shift;
            if ((part & 0x80) == 0)
            {
                if (shift != 0 && part == 0) throw new MsyncException("Noncanonical native IR immediate");
                return value;
            }
        }
        throw new MsyncException("Invalid native IR immediate");
    }

    private sealed class SectionStream(Stream source, long length) : Stream
    {
        private readonly long size = length;
        internal bool KnownAvailable => source.CanSeek;
        internal long Remaining { get; private set; } = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => size - Remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = source.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            Remaining -= count;
            return count;
        }
        public override int ReadByte()
        {
            if (Remaining == 0) return -1;
            int value = source.ReadByte();
            if (value >= 0) Remaining--;
            return value;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
