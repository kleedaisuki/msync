using System.Text;
using System.Runtime.InteropServices;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static ReadOnlySpan<byte> Magic => "MSYNCIR\0"u8;
    public const ushort FormatVersion = 2;
    public const string LanguageVersion = LanguageContract.Version;

    public static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    public static NativeProgram Read(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception error) when (error is IOException or MsyncException)
        {
            throw new MsyncException($"Cannot read native IR artifact '{path}': {error.Message}",
                (error as MsyncException)?.Span, error);
        }
    }

    public static NativeProgram Read(Stream stream) => Read(stream, null);

    public static NativeProgram Read(Stream stream, CompilationTrace? trace) => trace?.Detail is { } detail
        ? detail.MeasureDetailed("compiler.artifact.decode", (stream, detail), static state => ReadCore(state.stream, state.detail),
            size: CompilationSize.Of, metrics: CompilationMetrics.Of)
        : ReadCore(stream, null);

    private static NativeProgram ReadCore(Stream stream, CompilationTrace? trace)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using PositionStream input = new(stream);
        long start = input.Position;
        using BinaryReader reader = new(input, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new MsyncException("Not an msync native IR artifact");
            ushort version = reader.ReadUInt16();
            if (version != FormatVersion)
                throw new MsyncException($"Unsupported native IR format {version}; expected {FormatVersion}. Recompile the source with this msync version.");
            int encoding = reader.ReadInt32();
            if (encoding != CompactMarker) throw new MsyncException("Unsupported native IR encoding; recompile the source with this msync version.");
            NativeProgram program = ReadCompact(reader, trace);
            trace?.Metric("artifact.bytes", input.Position - start);
            return program;
        }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException)
        {
            throw new MsyncException($"Native IR artifact at byte {input.Position}: {error.Message}",
                (error as MsyncException)?.Span, error);
        }
    }

    public static void Write(NativeProgram program, string path)
    {
        ValidateExport(program);
        WritePath(program, path);
    }

    public static void WriteProduced(NativeProgram program, string path) => WriteProduced(program, path, null);

    public static void WriteProduced(NativeProgram program, string path, CompilationTrace? trace)
    {
        ValidateExport(program);
        WritePath(program, path, trace?.Detail);
    }

    public static void Write(NativeProgram program, Stream stream)
    {
        ValidateExport(program);
        Encode(program, stream);
    }

    public static void WriteProduced(NativeProgram program, Stream stream) => WriteProduced(program, stream, null);

    public static void WriteProduced(NativeProgram program, Stream stream, CompilationTrace? trace)
    {
        ValidateExport(program);
        Encode(program, stream, trace?.Detail);
    }

    private static void ValidateExport(NativeProgram program)
    {
        if (program.SessionRoots is { Count: > 0 } || program.SessionDispatches is { Count: > 0 })
            throw new MsyncException("Native IR with live session roots or open session call signatures cannot be exported. Compile a self-contained source instead.");
    }

    private static void WritePath(NativeProgram program, string path, CompilationTrace? trace = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!,
            $".{System.IO.Path.GetFileName(full)}.msync-{Guid.NewGuid():N}");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Encode(program, stream, trace);
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Encode(NativeProgram program, Stream stream, CompilationTrace? trace = null)
    {
        if (trace is null) { EncodeCompact(program, stream); return; }
        trace.Run("compiler.artifact.encode", (program, stream, trace),
            static state => EncodeCompact(state.program, state.stream, state.trace));
    }

    private static NativeConstant ReadConstant(BinaryReader reader) => ReadConstant(reader, (NativeKind)reader.ReadByte());

    private static NativeConstant ReadConstant(BinaryReader reader, NativeKind kind) => new(kind,
        ReadOptional(reader), BitConverter.Int64BitsToDouble(reader.ReadInt64()), reader.ReadBoolean(),
        reader.ReadBoolean() ? ReadType(reader) : null, (ConnectionKind)reader.ReadByte())
    { Path = reader.ReadBoolean() ? ReadPath(reader) : null, OriginId = reader.ReadInt32(), OriginOffset = reader.ReadInt32() };

    private static void WriteConstant(BinaryWriter writer, NativeConstant value)
    {
        writer.Write((byte)value.Kind); WriteOptional(writer, value.Text);
        writer.Write(BitConverter.DoubleToInt64Bits(value.Number)); writer.Write(value.Boolean);
        writer.Write(value.Type is not null);
        if (value.Type is { } type) WriteType(writer, type);
        writer.Write((byte)value.ConnectionKind);
        writer.Write(value.Path.HasValue);
        if (value.Path is { } path) WritePath(writer, path);
        writer.Write(value.OriginId); writer.Write(value.OriginOffset);
    }

    private static TypeRef ReadType(BinaryReader reader)
    {
        List<TypeKind> kinds = [];
        do { kinds.Add((TypeKind)reader.ReadUInt16()); } while (reader.ReadBoolean());
        TypeRef? result = null;
        for (int index = kinds.Count - 1; index >= 0; index--) result = new(kinds[index], result);
        return result!;
    }

    private static void WriteType(BinaryWriter writer, TypeRef type)
    {
        TypeRef? current = type;
        do
        {
            writer.Write((ushort)current.Kind); writer.Write(current.Element is not null);
            current = current.Element;
        } while (current is not null);
    }

    private static SourceSpan? ReadSpan(BinaryReader reader) => reader is CompactReader compact ? compact.Span() : reader.ReadBoolean()
        ? new(ReadText(reader), reader.ReadInt32(), reader.ReadInt32()) : null;

    private static void WriteSpan(BinaryWriter writer, SourceSpan? source)
    {
        if (writer is CompactWriter compact) { compact.Span(source); return; }
        writer.Write(source.HasValue);
        if (source is { } span) { WriteText(writer, span.Source); writer.Write(span.Start); writer.Write(span.Length); }
    }

    private static string? ReadOptional(BinaryReader reader) => reader.ReadBoolean() ? ReadText(reader) : null;
    private static void WriteOptional(BinaryWriter writer, string? value)
    { writer.Write(value is not null); if (value is not null) WriteText(writer, value); }

    private static string ReadText(BinaryReader reader)
    {
        if (reader is CompactReader compact) return compact.Text();
        return ReadText(reader, reader.ReadInt32());
    }

    private static string ReadText(BinaryReader reader, int count)
    {
        if (count < 0 || reader.BaseStream.CanSeek
            && (long)count * sizeof(char) > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new MsyncException("Native IR text length exceeds remaining artifact bytes");
        Span<char> chunk = stackalloc char[256];
        if (count <= chunk.Length)
        {
            ReadCharacters(reader, chunk[..count]);
            return new string(chunk[..count]);
        }
        StringBuilder result = new();
        while (count != 0)
        {
            int length = Math.Min(count, chunk.Length);
            Span<char> characters = chunk[..length];
            ReadCharacters(reader, characters);
            result.Append(characters);
            count -= length;
        }
        return result.ToString();
    }

    private static void ReadCharacters(BinaryReader reader, Span<char> characters)
    {
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(characters));
        if (!BitConverter.IsLittleEndian)
            for (int index = 0; index < characters.Length; index++)
                characters[index] = (char)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)characters[index]);
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        if (writer is CompactWriter compact) { compact.Text(value); return; }
        writer.Write(value.Length);
        if (BitConverter.IsLittleEndian) writer.Write(MemoryMarshal.AsBytes(value.AsSpan()));
        else foreach (char character in value) writer.Write((ushort)character);
    }

    private static T[]? ReadOptionalArray<T>(BinaryReader reader, Func<BinaryReader, T> read) =>
        reader.ReadBoolean() ? ReadArray(reader, read) : null;

    private static void WriteOptionalArray<T>(BinaryWriter writer, IReadOnlyList<T>? values, Action<BinaryWriter, T> write)
    { writer.Write(values is not null); if (values is not null) WriteArray(writer, values, write); }

    private static T[] ReadArray<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        int count = reader.ReadInt32();
        if (count < 0) throw new MsyncException("Negative native IR table length");
        if (count == 0) return [];
        if (reader is CompactReader compact && count > compact.Remaining)
            throw new MsyncException("Native IR table length exceeds remaining section bytes");
        if (reader.BaseStream.CanSeek && count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new MsyncException("Native IR table length exceeds remaining artifact bytes");
        if (reader.BaseStream.CanSeek || reader is CompactReader { CanPreallocate: true })
        {
            T[] values = new T[count];
            for (int index = 0; index < count; index++) values[index] = read(reader);
            return values;
        }
        List<T> streamed = [];
        for (int index = 0; index < count; index++) streamed.Add(read(reader));
        return streamed.ToArray();
    }

    private static void WriteArray<T>(BinaryWriter writer, IReadOnlyList<T> values, Action<BinaryWriter, T> write)
    { writer.Write(values.Count); foreach (T value in values) write(writer, value); }

    private sealed class PositionStream(Stream source) : Stream
    {
        private long position = source.CanSeek ? source.Position : 0;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => position; set => position = source.Position = value; }
        public override void Flush() => source.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        { int read = source.Read(buffer, offset, count); position += read; return read; }
        public override int Read(Span<byte> buffer)
        { int read = source.Read(buffer); position += read; return read; }
        public override int ReadByte()
        { int value = source.ReadByte(); if (value >= 0) position++; return value; }
        public override long Seek(long offset, SeekOrigin origin) => position = source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
