using System.Text;

namespace Msync.Core.Native;

public static partial class LegacyV6Reader
{
    private static ReadOnlySpan<byte> Magic => "MSYNCBC\0"u8;
    public const ushort FormatVersion = 6;
    public const ushort LegacyStorageMask = 2048;
    public static bool HasMagic(ReadOnlySpan<byte> input) => input.StartsWith(Magic);

    public static LegacyV6Module Read(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception error) when (error is IOException or MsyncException)
        {
            throw new MsyncException($"Cannot import format-6 artifact '{path}': {error.Message}",
                (error as MsyncException)?.Span, error);
        }
    }

    public static LegacyV6Module Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new MsyncException("Not an msync format-6 artifact");
            ushort version = reader.ReadUInt16();
            if (version != FormatVersion)
                throw new MsyncException($"Unsupported legacy bytecode format {version}; expected 6. Recompile the original source with this msync version.");
            string language = reader.ReadString();
            if (language != "0.8.0") throw new MsyncException($"Unsupported format-6 language semantics '{language}'");
            ModuleSemantics semantics = (ModuleSemantics)reader.ReadByte();
            ArtifactStage stage = (ArtifactStage)reader.ReadByte();
            int optimization = reader.ReadInt32(), entry = reader.ReadInt32();
            BindingContext? context = reader.ReadBoolean()
                ? new(reader.ReadString(), reader.ReadString(), Optional(reader)) : null;
            LegacyV6CodeType[] types = Array(reader, value => new LegacyV6CodeType(value.ReadUInt16(), value.ReadInt32(), Ids(value)));
            LegacyV6Layout[] layouts = Array(reader, value => new LegacyV6Layout(Array(value,
                field => new LegacyV6Field(field.ReadString(), field.ReadInt32()))));
            int[] statics = Ids(reader);
            LegacyV6Projection[] projections = Array(reader, value => new LegacyV6Projection(value.ReadString(),
                Array(value, item => new NativeProjectionCase(item.ReadInt32(), item.ReadInt32()))));
            LegacyV6Value[] constants = Array(reader, value => ReadValue(value, layouts));
            LegacyV6Function[] functions = Array(reader, ReadFunction);
            LegacyV6Plan[] plans = Array(reader, ReadPlan);
            LegacyV6Module module = new(types, layouts, statics, projections, constants, functions,
                plans, entry, stage, context, optimization, semantics);
            ValidateMetadata(module);
            return module;
        }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException)
        {
            string position = stream.CanSeek ? $" at byte {stream.Position}" : "";
            throw new MsyncException($"Format-6 artifact{position}: {error.Message}",
                (error as MsyncException)?.Span, error);
        }
    }

    private static LegacyV6Function ReadFunction(BinaryReader reader)
    {
        string name = reader.ReadString();
        int locals = reader.ReadInt32();
        LegacyV6Signature signature = new(Ids(reader), reader.ReadInt32(),
            Array(reader, value => new LegacyV6Capture(value.ReadInt32(), value.ReadBoolean())),
            Ids(reader), reader.ReadBoolean());
        LegacyV6Instruction[] instructions = Array(reader, value => new LegacyV6Instruction(
            (LegacyV6Op)value.ReadByte(), value.ReadInt32(), value.ReadInt32(), Optional(value), Span(value)));
        return new(name, locals, signature, instructions);
    }

    private static LegacyV6Plan ReadPlan(BinaryReader reader)
    {
        int function = reader.ReadInt32(), start = reader.ReadInt32(), end = reader.ReadInt32();
        int eliminated = reader.ReadInt32();
        int[]? results = reader.ReadBoolean() ? Ids(reader) : null;
        LegacyV6Transfer[] transfers = Array(reader, value =>
        {
            int id = value.ReadInt32();
            LegacyV6Value source = ReadValue(value, []);
            LegacyV6Value destination = ReadValue(value, []);
            if (destination.Kind != LegacyV6ValueKind.Path)
                throw new MsyncException("A format-6 execution-plan destination must be a Path");
            PlannedTransferKind kind = (PlannedTransferKind)value.ReadByte();
            TransferDestinationMode mode = (TransferDestinationMode)value.ReadByte();
            PlanResource[] reads = Array(value, Resource), writes = Array(value, Resource);
            int[] predecessors = Ids(value);
            return new LegacyV6Transfer(id, source, destination.Path!.Value, kind, mode,
                reads, writes, predecessors, Span(value));
        });
        ExecutionBatch[] batches = Array(reader, value =>
        {
            string endpoint = value.ReadString();
            return new ExecutionBatch(Ids(value), endpoint);
        });
        return new(function, start, end, eliminated, results, transfers, batches);
    }

    private static LegacyV6Value ReadValue(BinaryReader reader, IReadOnlyList<LegacyV6Layout> layouts)
    {
        Stack<ValueFrame> pending = new();
        while (true)
        {
            byte tag = reader.ReadByte();
            LegacyV6Value value;
            switch (tag)
            {
                case 0: value = new(LegacyV6ValueKind.Null); break;
                case 1: value = new(LegacyV6ValueKind.Bool, Boolean: reader.ReadBoolean()); break;
                case 2: value = new(LegacyV6ValueKind.String, Text: reader.ReadString()); break;
                case 3: value = new(LegacyV6ValueKind.Number, Number: reader.ReadDouble()); break;
                case 4: value = new(LegacyV6ValueKind.Path, Path: ReadPath(reader)); break;
                case 5: value = new(LegacyV6ValueKind.Connection, Connection: ReadConnection(reader)); break;
                case 6: value = new(LegacyV6ValueKind.Type, Type: ReadTypeValue(reader)); break;
                case 7:
                    int function = reader.ReadInt32(), captures = Count(reader);
                    if (captures != 0) { pending.Push(new(LegacyV6ValueKind.Function, captures, function: function)); continue; }
                    value = new(LegacyV6ValueKind.Function, Function: function,
                        NativeName: Optional(reader), HasEnvironment: true, Items: []);
                    break;
                case 8:
                    int layout = reader.ReadInt32();
                    Index(layout, layouts.Count, "constant namespace layout");
                    int fields = layouts[layout].Fields.Count;
                    if (fields != 0) { pending.Push(new(LegacyV6ValueKind.Namespace, fields, layout: layout)); continue; }
                    value = new(LegacyV6ValueKind.Namespace, Layout: layout, Items: []);
                    break;
                case 9:
                    LegacyV6TypeValue type = ReadTypeValue(reader);
                    int items = Count(reader);
                    if (items != 0) { pending.Push(new(LegacyV6ValueKind.Range, items, type: type)); continue; }
                    value = new(LegacyV6ValueKind.Range, Type: type, Items: []);
                    break;
                case 10: value = new(LegacyV6ValueKind.Function, Function: reader.ReadInt32(), NativeName: Optional(reader)); break;
                default: throw new MsyncException($"Unknown format-6 constant tag {tag}");
            }
            while (pending.TryPeek(out ValueFrame? parent))
            {
                parent.Items[parent.Next++] = value;
                if (parent.Next != parent.Items.Length) break;
                pending.Pop();
                value = parent.Kind switch
                {
                    LegacyV6ValueKind.Function => new(parent.Kind, Function: parent.Function,
                        NativeName: Optional(reader), HasEnvironment: true, Items: parent.Items),
                    LegacyV6ValueKind.Namespace => new(parent.Kind, Layout: parent.Layout, Items: parent.Items),
                    LegacyV6ValueKind.Range => new(parent.Kind, Type: parent.Type, Items: parent.Items),
                    _ => throw new InvalidOperationException("Unknown portable recipe container.")
                };
            }
            if (pending.Count == 0) return value;
        }
    }

    private sealed class ValueFrame(LegacyV6ValueKind kind, int count, int function = -1,
        int layout = -1, LegacyV6TypeValue? type = null)
    {
        internal LegacyV6ValueKind Kind { get; } = kind;
        internal int Function { get; } = function;
        internal int Layout { get; } = layout;
        internal LegacyV6TypeValue? Type { get; } = type;
        internal LegacyV6Value[] Items { get; } = new LegacyV6Value[count];
        internal int Next { get; set; }
    }

    private static LegacyV6TypeValue ReadTypeValue(BinaryReader reader)
    {
        List<ushort> kinds = [];
        while (true)
        {
            ushort kind = reader.ReadUInt16();
            if ((kind & ~4095) != 0) throw new MsyncException($"Invalid format-6 type mask {kind}");
            kinds.Add(kind);
            if (!reader.ReadBoolean()) break;
        }
        LegacyV6TypeValue? result = null;
        for (int index = kinds.Count - 1; index >= 0; index--) result = new(kinds[index], result);
        return result!;
    }
    private static NativePath ReadPath(BinaryReader reader)
    {
        string raw = reader.ReadString();
        string? cwd = Optional(reader);
        NativeConnection? connection = reader.ReadBoolean() ? ReadConnection(reader) : null;
        return new(raw, cwd, connection, Optional(reader), Optional(reader));
    }
    private static NativeConnection ReadConnection(BinaryReader reader)
    {
        ConnectionKind kind = (ConnectionKind)reader.ReadByte();
        if (!Enum.IsDefined(kind)) throw new MsyncException($"Unknown format-6 connection kind {(byte)kind}");
        return new(kind, reader.ReadString());
    }
    private static PlanResource Resource(BinaryReader reader) => new(reader.ReadString(), reader.ReadString(), reader.ReadBoolean());
    private static SourceSpan? Span(BinaryReader reader) => reader.ReadBoolean()
        ? new(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32()) : null;
    private static string? Optional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static int[] Ids(BinaryReader reader) => Array(reader, value => value.ReadInt32());
    private static int Count(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || reader.BaseStream.CanSeek && count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new MsyncException("Invalid format-6 table length");
        return count;
    }
    private static T[] Array<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        T[] result = new T[Count(reader)];
        for (int index = 0; index < result.Length; index++) result[index] = read(reader);
        return result;
    }
    private static void Index(int index, int count, string description)
    {
        if ((uint)index >= (uint)count) throw new MsyncException($"Invalid format-6 {description} index {index}");
    }
}
