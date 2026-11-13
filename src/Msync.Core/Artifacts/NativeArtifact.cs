using System.Text;
using System.Runtime.InteropServices;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static ReadOnlySpan<byte> Magic => "MSYNCIR\0"u8;
    public const ushort FormatVersion = 1;
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
                throw new MsyncException($"Unsupported native IR format {version}; expected {FormatVersion}");
            int encoding = reader.ReadInt32();
            NativeProgram program = encoding == CompactMarker ? ReadCompact(reader, trace) : ReadUnpooledCompatible(reader, encoding);
            Validate(program, trace);
            trace?.Metric("artifact.bytes", input.Position - start);
            return trace is null ? NormalizeAllocationHints(program)
                : trace.MeasureDetailed("compiler.artifact.allocation_hints", program, NormalizeAllocationHints);
        }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException)
        {
            throw new MsyncException($"Native IR artifact at byte {input.Position}: {error.Message}",
                (error as MsyncException)?.Span, error);
        }
    }

    private static NativeProgram ReadUnpooledCompatible(BinaryReader reader, int languageLength)
    {
        // Early format 1 added a per-function reflection field without an encoding revision.
        // Only that old payload needs layout detection; compact encodings are explicitly versioned.
        if (!reader.BaseStream.CanSeek)
        {
            using MemoryStream buffered = new();
            reader.BaseStream.CopyTo(buffered);
            buffered.Position = 0;
            using BinaryReader replay = new(buffered, Encoding.UTF8, leaveOpen: true);
            return ReadUnpooledCompatible(replay, languageLength);
        }
        long start = reader.BaseStream.Position;
        try { return ReadUnpooled(reader, languageLength, reflection: true); }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException)
        {
            reader.BaseStream.Position = start;
            try { return ReadUnpooled(reader, languageLength, reflection: false); }
            catch (Exception fallback) when (fallback is MsyncException or IOException or ArgumentException or OverflowException)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); throw; }
        }
    }

    private static NativeProgram ReadUnpooled(BinaryReader reader, int languageLength, bool reflection)
    {
        string language = ReadText(reader, languageLength);
        if (language != LanguageVersion)
            throw new MsyncException($"Unsupported native IR language semantics '{language}'; expected '{LanguageVersion}'");
        ModuleSemantics semantics = (ModuleSemantics)reader.ReadByte();
        ArtifactStage stage = (ArtifactStage)reader.ReadByte();
        int optimization = reader.ReadInt32();
        int entry = reader.ReadInt32();
        bool preserveLocals = reader.ReadBoolean();
        BindingContext? context = reader.ReadBoolean()
            ? new(ReadText(reader), ReadText(reader), ReadOptional(reader)) : null;
        NativeCodeType[] types = ReadArray(reader, value => new NativeCodeType(
            (TypeKind)value.ReadUInt16(), value.ReadInt32(), ReadOptionalArray(value, item => item.ReadInt32()), value.ReadInt32()));
        NativeCodeLayout[] layouts = ReadArray(reader, value => new NativeCodeLayout(
            ReadArray(value, field => new NativeCodeField(ReadText(field), field.ReadInt32()))));
        NativeProjection[] projections = ReadArray(reader, value => new NativeProjection(
            ReadArray(value, item => new NativeProjectionCase(item.ReadInt32(), item.ReadInt32()))));
        NativeStatic[] statics = ReadArray(reader, value => new NativeStatic(value.ReadInt32(), value.ReadInt32()));
        NativeConstant[] constants = ReadArray(reader, ReadConstant);
        NativeFunctionCode[] functions = ReadArray(reader, value => ReadFunction(value, reflection));
        NativeAllocationPlan[] allocations = ReadArray(reader, value => new NativeAllocationPlan(
            value.ReadInt32(), value.ReadInt32(), (NativeAllocationPlacement)value.ReadByte(), value.ReadInt32()));
        NativeExecutionPlan[] plans = ReadArray(reader, ReadPlan);
        NativeProgram program = new(constants, functions, types, layouts, entry)
        {
            Projections = projections,
            Statics = statics,
            Allocations = allocations,
            Context = context,
            Stage = stage,
            Semantics = semantics,
            OptimizationLevel = optimization,
            PreserveLocals = preserveLocals,
            ExecutionPlans = plans.Length == 0 ? null : plans
        };
        return program;
    }

    public static void Write(NativeProgram program, string path)
    {
        ValidateExport(program);
        Validate(program);
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
        Validate(program);
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

    private static NativeProgram NormalizeAllocationHints(NativeProgram program)
    {
        if (!program.Allocations.Any(plan => plan.Placement != NativeAllocationPlacement.LogicalHeap)) return program;
        Dictionary<int, IReadOnlySet<int>> safe = [];
        NativeAddressAnalysis.ProgramFacts facts = new(program);
        NativeAllocationPlan[] plans = new NativeAllocationPlan[program.Allocations.Count];
        for (int index = 0; index < plans.Length; index++)
        {
            NativeAllocationPlan plan = program.Allocations[index];
            if (plan.Placement == NativeAllocationPlacement.Frame)
            {
                if (!safe.TryGetValue(plan.Function, out IReadOnlySet<int>? proven))
                { proven = NativeAllocationSafety.FrameSafe(program, plan.Function, facts); safe.Add(plan.Function, proven); }
                if (!proven.Contains(plan.Instruction)) plan = plan with { Placement = NativeAllocationPlacement.LogicalHeap };
            }
            else if (plan.Placement == NativeAllocationPlacement.Eliminated
                && program.Functions[plan.Function].Instructions[plan.Instruction].Op == NativeOp.Allocate)
                plan = plan with { Placement = NativeAllocationPlacement.LogicalHeap };
            plans[index] = plan;
        }
        return program with { Allocations = plans };
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

    private static NativeFunctionCode ReadFunction(BinaryReader reader, bool includesReflection)
    {
        string name = ReadText(reader);
        int parameters = reader.ReadInt32(), captures = reader.ReadInt32(), iterators = reader.ReadInt32();
        bool template = reader.ReadBoolean();
        NativeSlot[] slots = ReadArray(reader, value => new NativeSlot(value.ReadInt32(),
            (NativeSlotRole)value.ReadByte(), value.ReadBoolean(), ReadOptional(value)));
        NativeSignature signature = new(ReadArray(reader, value => value.ReadInt32()), reader.ReadInt32(),
            ReadArray(reader, value => new NativeCaptureContract(value.ReadInt32(), (NativeCaptureMode)value.ReadByte())));
        NativeInstruction[] instructions = ReadArray(reader, ReadInstruction);
        NativeFunctionReflectionSignature? reflection = includesReflection && reader.ReadBoolean()
            ? new(ReadArray(reader, ReadType), ReadType(reader)) : null;
        return new(name, slots, instructions, signature, parameters, captures, iterators, template)
        { ReflectionSignature = reflection };
    }

    private static NativeInstruction ReadInstruction(BinaryReader reader) => new((NativeOp)reader.ReadByte(),
        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
        reader.ReadInt64(), ReadOptional(reader), ReadOptionalArray(reader, value => value.ReadInt32()), ReadSpan(reader));

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
