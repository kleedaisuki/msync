using System.Text;

namespace Msync.Core.Hpir;

public static partial class HpirArtifact
{
    // Portable and bound images share the physical instruction stream.
    // Execution expects a producer-authored image; wire checks are not a proof of guest memory safety.
    private static ReadOnlySpan<byte> Magic => "MSHPIR\0"u8;
    public const ushort FormatVersion = 8;

    public static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    public static void WriteProduced(HpirProgram program, string path, CompilationTrace? trace = null) =>
        ArtifactFile.Write(path, (program, trace), static (state, stream) => WriteProduced(state.program, stream, state.trace));

    public static void WriteProduced(HpirProgram program, Stream stream, CompilationTrace? trace = null)
    {
        if (trace?.Detail is not { } detail) Write(program, stream, null);
        else detail.Run("compiler.hpir.image.encode", (program, stream, detail),
            static state => Write(state.program, state.stream, state.detail));
    }

    public static HpirProgram ReadTrusted(Stream stream, CompilationTrace? trace = null)
    {
        if (trace?.Detail is not { } detail) return Read(stream, null);
        return detail.MeasureDetailed("compiler.hpir.image.decode", (stream, detail), static state => Read(state.stream, state.detail));
    }

    private static void Write(HpirProgram program, Stream stream, CompilationTrace? trace)
    {
        ValidateStage(program);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write((byte)program.Stage);
        writer.Write((byte)(program.OptimizationLevel + 1));
        writer.Write(program.EntryPoint);
        WriteArray(writer, program.Layouts, static (output, layout) =>
        {
            output.Write(layout.Size);
            output.Write(layout.Alignment);
            WriteOffsets(output, layout.PointerOffsets);
            WriteOffsets(output, layout.BorrowOffsets);
        });
        WriteArray(writer, program.Globals, static (output, global) =>
        {
            output.Write(global.Declaration);
            output.Write(global.Layout);
        });
        WriteArray(writer, program.Imports, static (output, import) =>
        {
            output.Write((byte)import.Service);
            output.Write(import.Binding);
            output.Write((byte)import.Result);
            WriteArray(output, import.Arguments, static (binary, width) => binary.Write((byte)width));
            if (import.Service == HpirService.ProcessStart)
                WriteArray(output, import.ProcessArguments, static (binary, argument) => binary.Write((byte)argument));
        });
        WriteArray(writer, program.TextLiterals, WriteText);
        WriteArray(writer, program.TypeLiterals, static (output, type) =>
        {
            output.Write((ushort)type.Kinds);
            output.Write(type.Element);
        });
        WriteOffsets(writer, program.TypeCells);
        writer.Write(program.TextLayout);
        writer.Write(program.PathAnchors);
        if (program.Stage == HpirStage.Executable) WriteContext(writer, program.Context);
        WriteArray(writer, program.Functions, WriteFunction);
        WriteEffects(writer, program);
        WriteContextChecks(writer, program.ContextChecks);
        WriteIteratorSelections(writer, program.IteratorSelections);
        trace?.Metric("functions", program.Functions.Count);
        trace?.Metric("globals", program.Globals.Count);
        trace?.Metric("literals.text", program.TextLiterals.Count);
        trace?.Metric("literals.type", program.TypeLiterals.Count);
        trace?.Metric("effects.relocatable", program.TransferInputs.Count);
        trace?.Metric("effects.prepared", program.PreparedTransfers.Count);
        trace?.Metric("effects.context_checks", program.ContextChecks.Count);
        if (program.IteratorSelections.Count != 0) trace?.Metric("iterator.selection_recipes", program.IteratorSelections.Count);
    }

    private static HpirProgram Read(Stream stream, CompilationTrace? trace)
    {
        try
        {
            using var reader = new ImageReader(stream);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new MsyncException("Not an HPIR image");
            ushort version = reader.ReadUInt16();
            if (version != FormatVersion)
                throw new MsyncException($"Unsupported HPIR image format {version}; expected {FormatVersion}. Recompile the source.");
            var stage = (HpirStage)reader.ReadByte();
            if (stage is not (HpirStage.Relocatable or HpirStage.Executable))
                throw new MsyncException($"Invalid HPIR artifact stage {(int)stage}");
            int optimization = reader.ReadByte() - 1;
            int entry = reader.ReadInt32();
            var layouts = ReadArray(reader, static input => new HpirLayout(input.ReadInt32(), input.ReadInt32(),
                ReadOffsets(input)) { BorrowOffsets = ReadOffsets(input) }, 16);
            var globals = ReadArray(reader, static input => new HpirGlobal(input.ReadInt32(), input.ReadInt32()), 8);
            var imports = ReadArray(reader, ReadImport, 10);
            string[] literals = ReadArray(reader, ReadText, 4);
            var types = ReadArray(reader, static input => new HpirTypeLiteral((TypeKind)input.ReadUInt16(), input.ReadInt32()), 6);
            int[] typeCells = ReadOffsets(reader);
            int textLayout = reader.ReadInt32();
            bool pathAnchors = reader.ReadBoolean();
            BindingContext? context = stage == HpirStage.Executable ? ReadContext(reader) : null;
            var functions = ReadArray(reader, ReadFunction, 29);
            var program = ReadEffects(reader, new(functions, entry)
            {
                Layouts = layouts, Globals = globals, Imports = imports, Stage = stage, TextLiterals = literals,
                TypeLiterals = types, TypeCells = typeCells, TextLayout = textLayout, PathAnchors = pathAnchors,
                Context = context, OptimizationLevel = optimization
            });
            program = program with { ContextChecks = ReadContextChecks(reader), IteratorSelections = ReadIteratorSelections(reader) };
            if (reader.Bounded && stream.Position != stream.Length)
                throw new MsyncException("Unexpected data after the HPIR image");
            trace?.Metric("input.length_queries", reader.Bounded ? 2 : 0);
            trace?.Metric("input.bounded", reader.Bounded ? 1 : 0);
            Validate(program, trace);
            trace?.Metric("functions", functions.Length);
            trace?.Metric("globals", globals.Length);
            trace?.Metric("literals.text", literals.Length);
            trace?.Metric("literals.type", types.Length);
            trace?.Metric("effects.context_checks", program.ContextChecks.Count);
            if (program.IteratorSelections.Count != 0) trace?.Metric("iterator.selection_recipes", program.IteratorSelections.Count);
            return program;
        }
        catch (Exception error) when (error is IOException or FormatException or OverflowException)
        {
            throw new MsyncException($"Invalid HPIR image: {error.Message}", inner: error);
        }
    }

    private static HpirImport ReadImport(ImageReader input)
    {
        var import = new HpirImport((HpirService)input.ReadByte(), input.ReadInt32(), (HpirWidth)input.ReadByte(),
            ReadArray(input, static body => (HpirWidth)body.ReadByte(), 1));
        return import.Service != HpirService.ProcessStart ? import : import with
        { ProcessArguments = ReadArray(input, static body => (HpirProcessArgument)body.ReadByte(), 1) };
    }

    private static void WriteFunction(BinaryWriter writer, HpirFunction function)
    {
        WriteText(writer, function.Name);
        writer.Write(function.FrameBytes);
        writer.Write((byte)function.Result);
        WriteArray(writer, function.Parameters, static (output, parameter) =>
        {
            output.Write(parameter.Offset);
            output.Write((byte)parameter.Width);
        });
        WriteArray(writer, function.Instructions, static (output, instruction) =>
        {
            output.Write((byte)instruction.Op);
            output.Write(instruction.D);
            output.Write(instruction.A);
            output.Write(instruction.B);
            output.Write(instruction.Immediate);
        });
        WriteArray(writer, function.Calls, static (output, call) =>
        {
            output.Write(call.Target);
            WriteArray(output, call.Arguments, static (binary, argument) => binary.Write(argument));
        });
        WriteArray(writer, function.Sources, static (output, source) =>
        {
            output.Write(source.Instruction);
            WriteText(output, source.Source.Source);
            output.Write(source.Source.Start);
            output.Write(source.Source.Length);
        });
        WriteArray(writer, function.Safepoints, static (output, map) =>
        {
            output.Write(map.Instruction);
            WriteOffsets(output, map.RootOffsets);
            WriteOffsets(output, map.BorrowOffsets);
        });
    }

    private static HpirFunction ReadFunction(ImageReader reader)
    {
        string name = ReadText(reader);
        int frame = reader.ReadInt32();
        var result = (HpirWidth)reader.ReadByte();
        var parameters = ReadArray(reader, static input => new HpirParameter(input.ReadInt32(), (HpirWidth)input.ReadByte()), 5);
        var instructions = ReadArray(reader, static input => new HpirInstruction((HpirOp)input.ReadByte(), input.ReadInt32(),
            input.ReadInt32(), input.ReadInt32(), input.ReadInt64()), 21);
        var calls = ReadArray(reader, static input => new HpirCall(input.ReadInt32(), ReadArray(input, static body => body.ReadInt32(), 4)), 8);
        var sources = ReadArray(reader, static input => new HpirSource(input.ReadInt32(),
            new(ReadText(input), input.ReadInt32(), input.ReadInt32())), 16);
        var maps = ReadArray(reader, static input => new HpirRootMap(input.ReadInt32(),
            ReadOffsets(input)) { BorrowOffsets = ReadOffsets(input) }, 12);
        return new(name, frame, result, parameters, instructions, calls, sources) { Safepoints = maps };
    }

    private static int[] ReadOffsets(ImageReader reader) => ReadArray(reader, static input => input.ReadInt32(), 4);
    private static void WriteOffsets(BinaryWriter writer, IReadOnlyList<int> offsets) =>
        WriteArray(writer, offsets, static (output, offset) => output.Write(offset));

    private static void WriteArray<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
    {
        writer.Write(items.Count);
        foreach (var item in items) write(writer, item);
    }

    private static T[] ReadArray<T>(ImageReader reader, Func<ImageReader, T> read, int itemBytes)
    {
        int count = reader.ReadInt32();
        if (count < 0) throw new MsyncException("Negative HPIR image table length");
        if (count == 0) return [];
        if (reader.Bounded)
        {
            if (count > reader.Remaining / itemBytes)
                throw new MsyncException("HPIR image table length exceeds remaining data");
            var items = new T[count];
            for (int index = 0; index < count; ++index) items[index] = read(reader);
            return items;
        }
        var decoded = new List<T>();
        for (int index = 0; index < count; ++index) decoded.Add(read(reader));
        return decoded.ToArray();
    }

    private sealed class ImageReader(Stream stream) : BinaryReader(stream, Encoding.UTF8, leaveOpen: true)
    {
        // FileStream.Length may query the kernel. All table bounds in this
        // decode use the same extent; ReadExactly and the final end check
        // still detect truncation or extra data.
        private readonly long? extent = stream.CanSeek ? stream.Length : null;

        internal bool Bounded => extent.HasValue;
        internal long Remaining => extent.GetValueOrDefault() - BaseStream.Position;
    }

}
