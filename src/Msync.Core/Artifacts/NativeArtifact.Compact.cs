using System.Text;

namespace Msync.Core.Native;

public static partial class NativeArtifact
{
    private static void EncodeCompact(NativeProgram program, Stream stream, CompilationTrace? trace = null)
    {
        CompactPool pool = new();
        List<(CompactSection Kind, byte[] Bytes)> sections = [];
        Add(CompactSection.Header, writer => WriteCompactHeader(writer, program));
        Add(CompactSection.Types, writer => WriteArray(writer, program.Types, static (output, value) =>
        {
            output.Write(value.WireKind); output.Write(value.WireElement);
            WriteOptionalArray(output, value.WireLayouts, static (item, layout) => item.Write(layout));
            output.Write(value.Pointee);
        }));
        Add(CompactSection.Layouts, writer => WriteArray(writer, program.Layouts, static (output, value) =>
            WriteArray(output, value.Fields, static (item, field) => { WriteText(item, field.Name); item.Write(field.Type); })));
        Add(CompactSection.Projections, writer => WriteArray(writer, program.Projections, static (output, value) =>
            WriteArray(output, value.Cases, static (item, alternative) => { item.Write(alternative.Layout); item.Write(alternative.Field); })));
        Add(CompactSection.Statics, writer => WriteArray(writer, program.Statics,
            static (output, value) => { output.Write(value.Layout); output.Write(value.Declaration); }));
        Add(CompactSection.Constants, writer => WriteArray(writer, program.Constants, WriteConstant));
        Add(CompactSection.Functions, writer => WriteArray(writer, program.Functions, WriteCompactFunction));
        Add(CompactSection.Debug, writer => WriteCompactDebug(writer, program.Functions));
        Add(CompactSection.Code, writer =>
        {
            foreach (NativeFunctionCode function in program.Functions)
                foreach (NativeInstruction instruction in function.Instructions) WriteCompactInstruction(writer, instruction);
        });
        Add(CompactSection.Allocations, writer => WriteArray(writer, program.Allocations, static (output, value) =>
        {
            output.Write(value.Instruction); output.Write(value.Layout);
            output.Write((byte)value.Placement); output.Write(value.Function);
        }));
        Add(CompactSection.Plans, writer => WriteArray(writer, program.ExecutionPlans ?? [], WritePlan));
        Add(CompactSection.Backend, writer => WriteBackend(writer, program.Backend));

        trace?.Metric("pool.strings", pool.Strings.Count);
        trace?.Metric("pool.source_spans", pool.Spans.Count);
        trace?.Metric("sections", sections.Count);
        trace?.Metric("sections.bytes", sections.Sum(section => (long)section.Bytes.Length));
        RecordMachineTypes(program.Types, trace);
        using BinaryWriter output = new(stream, Encoding.UTF8, leaveOpen: true);
        output.Write(Magic); output.Write(FormatVersion); output.Write(CompactMarker); output.Write(CompactRevision);
        WriteUnsigned(output, checked((uint)pool.Strings.Count));
        foreach (string value in pool.Strings) WriteText(output, value);
        WriteUnsigned(output, checked((uint)pool.Spans.Count));
        foreach (SourceSpan span in pool.Spans)
        {
            WriteUnsigned(output, checked((uint)pool.String(span.Source)));
            output.Write(span.Start); output.Write(span.Length);
        }
        WriteUnsigned(output, checked((uint)sections.Count));
        foreach (var section in sections)
        {
            output.Write((byte)section.Kind); WriteUnsigned(output, checked((uint)section.Bytes.Length));
            output.Write(section.Bytes);
        }

        void Add(CompactSection kind, Action<CompactWriter> write)
        {
            byte[] bytes = trace is null ? EncodeSection(write)
                : trace.MeasureDetailed("compiler.artifact.encode_section", write, EncodeSection,
                    attributes: new Dictionary<string, string> { ["artifact.section"] = kind.ToString() },
                    metrics: static result => new Dictionary<string, ulong> { ["section.bytes"] = (ulong)result.Length });
            sections.Add((kind, bytes));
        }

        byte[] EncodeSection(Action<CompactWriter> write)
        {
            using MemoryStream section = new();
            using (CompactWriter writer = new(section, pool)) write(writer);
            return section.ToArray();
        }
    }

    private static void WriteCompactHeader(CompactWriter writer, NativeProgram program)
    {
        writer.Text(LanguageVersion); writer.Write((byte)program.Semantics); writer.Write((byte)program.Stage);
        writer.Write(program.OptimizationLevel); writer.Write(program.EntryPoint); writer.Write(program.PreserveLocals);
        writer.Write(program.Context is not null);
        if (program.Context is not { } context) return;
        writer.Text(context.WorkingDirectory); writer.Text(context.HomeDirectory); WriteOptional(writer, context.SourceFile);
    }

    private static void WriteCompactFunction(BinaryWriter writer, NativeFunctionCode function)
    {
        WriteText(writer, function.Name); writer.Write(function.ParameterCount); writer.Write(function.CaptureCount);
        writer.Write(function.IteratorCount); writer.Write(function.IsTemplate); writer.Write(function.Instructions.Count);
        WriteArray(writer, function.Slots, static (output, slot) =>
        { output.Write(slot.Type); output.Write((byte)slot.Role); output.Write(slot.Escapes); WriteOptional(output, slot.Name); });
        WriteArray(writer, function.Signature.Parameters, static (output, type) => output.Write(type));
        writer.Write(function.Signature.Result);
        WriteArray(writer, function.Signature.Captures, static (output, capture) =>
        { output.Write(capture.Type); output.Write((byte)capture.Mode); });
        writer.Write(function.ReflectionSignature is not null);
        if (function.ReflectionSignature is { } reflection)
        {
            WriteArray(writer, reflection.Parameters, WriteType); WriteType(writer, reflection.Result);
        }
        NativeStoragePlan storage = function.Storage!;
        WriteArray(writer, storage.Slots, static (output, value) => output.Write((byte)value));
        WriteArray(writer, storage.Instructions, static (output, value) => output.Write((byte)value));
        WriteArray(writer, storage.Calls, static (output, call) =>
        {
            output.Write(call.Instruction); output.Write(call.Snapshot);
            WriteArray(output, call.Borrowed, static (target, value) => target.Write(value));
        });
    }

    private static void WriteCompactDebug(CompactWriter writer, IReadOnlyList<NativeFunctionCode> functions)
    {
        foreach (NativeFunctionCode function in functions)
        {
            writer.Unsigned(checked((uint)function.Instructions.Count(instruction => instruction.Source.HasValue)));
            int previous = -1;
            for (int pc = 0; pc < function.Instructions.Count; pc++)
            {
                if (function.Instructions[pc].Source is not { } span) continue;
                writer.Unsigned(checked((uint)(pc - previous))); writer.Span(span); previous = pc;
            }
        }
    }

    private static NativeProgram ReadCompact(BinaryReader input, CompilationTrace? trace = null)
    {
        byte revision = input.ReadByte();
        if (revision != CompactRevision) throw new MsyncException($"Unsupported native IR compact encoding {revision}; expected {CompactRevision}. Recompile the artifact from source.");
        int stringCount = ReadPoolCount(input, "string", 4);
        List<string> stringValues = [];
        for (int index = 0; index < stringCount; index++) stringValues.Add(ReadText(input));
        string[] strings = stringValues.ToArray();
        int spanCount = ReadPoolCount(input, "source span", 9);
        List<SourceSpan> spanValues = [];
        for (int index = 0; index < spanCount; index++)
        {
            uint source = ReadUnsigned(input);
            if (source >= strings.Length) throw new MsyncException("Invalid native IR source file pool index");
            SourceSpan span = new(strings[(int)source], input.ReadInt32(), input.ReadInt32());
            ValidateSpan(span); spanValues.Add(span);
        }
        SourceSpan[] spans = spanValues.ToArray();
        trace?.Metric("pool.strings", strings.Length);
        trace?.Metric("pool.source_spans", spans.Length);
        uint sections = ReadUnsigned(input);
        if (sections != (uint)CompactSection.Backend) throw new MsyncException("Native IR compact section set is incomplete or unknown");
        (ModuleSemantics semantics, ArtifactStage stage, int optimization, int entry, bool preserve, BindingContext? context)
            = ReadSection(CompactSection.Header, ReadCompactHeader);
        NativeCodeType[] types = ReadSection(CompactSection.Types, reader => ReadArray(reader, value => NativeCodeType.FromWire(
            value.ReadUInt16(), value.ReadInt32(), ReadOptionalArray(value, item => item.ReadInt32()), value.ReadInt32())));
        RecordMachineTypes(types, trace);
        NativeCodeLayout[] layouts = ReadSection(CompactSection.Layouts, reader => ReadArray(reader, value => new NativeCodeLayout(
            ReadArray(value, field => new NativeCodeField(ReadText(field), field.ReadInt32())))));
        NativeProjection[] projections = ReadSection(CompactSection.Projections, reader => ReadArray(reader, value => new NativeProjection(
            ReadArray(value, item => new NativeProjectionCase(item.ReadInt32(), item.ReadInt32())))));
        NativeStatic[] statics = ReadSection(CompactSection.Statics, reader => ReadArray(reader,
            value => new NativeStatic(value.ReadInt32(), value.ReadInt32())));
        NativeConstant[] constants = ReadSection(CompactSection.Constants, reader => ReadArray(reader, ReadConstant));
        NativeFunctionCode[] functions = ReadSection(CompactSection.Functions, reader => ReadArray(reader, value => ReadCompactFunction(value)));
        SourceMapping[][] debug = ReadSection(CompactSection.Debug, reader => ReadCompactDebug(reader, functions, spans));
        _ = ReadSection(CompactSection.Code, reader => ReadCompactCode(reader, functions, debug, spans));
        NativeAllocationPlan[] allocations = ReadSection(CompactSection.Allocations, reader => ReadArray(reader, value => new NativeAllocationPlan(
            value.ReadInt32(), value.ReadInt32(), (NativeAllocationPlacement)value.ReadByte(), value.ReadInt32())));
        NativeExecutionPlan[] plans = ReadSection(CompactSection.Plans, reader => ReadArray(reader, ReadPlan));
        NativeBackendCode? backend = ReadSection(CompactSection.Backend, ReadBackend);
        NativeProgram program = new(constants, functions, types, layouts, entry)
        {
            Backend = backend,
            Projections = projections,
            Statics = statics,
            Allocations = allocations,
            Semantics = semantics,
            Stage = stage,
            OptimizationLevel = optimization,
            PreserveLocals = preserve,
            Context = context,
            ExecutionPlans = plans.Length == 0 ? null : plans
        };
        return program;

        T ReadSection<T>(CompactSection expected, Func<CompactReader, T> read)
        {
            CompactSection actual = (CompactSection)input.ReadByte();
            if (actual != expected) throw new MsyncException($"Expected native IR section {expected}, received {actual}");
            uint length = ReadUnsigned(input);
            if (input.BaseStream.CanSeek && length > input.BaseStream.Length - input.BaseStream.Position)
                throw new MsyncException($"Native IR section {expected} exceeds remaining artifact bytes");
            using SectionStream section = new(input.BaseStream, length);
            using CompactReader reader = new(section, strings, spans);
            return trace is null ? DecodeSection(reader, section, read, expected)
                : trace.MeasureDetailed("compiler.artifact.decode_section", (reader, section, read, expected, length, trace),
                    static state =>
                    {
                        T result = DecodeSection(state.reader, state.section, state.read, state.expected);
                        state.trace.Metric("section.bytes", state.length);
                        return result;
                    }, attributes: new Dictionary<string, string> { ["artifact.section"] = expected.ToString() });
        }
    }

    private static T DecodeSection<T>(CompactReader reader, SectionStream section, Func<CompactReader, T> read,
        CompactSection expected)
    {
        T result = read(reader);
        if (section.Remaining != 0) throw new MsyncException($"Unconsumed bytes in native IR section {expected}");
        return result;
    }

    private static int ReadPoolCount(BinaryReader reader, string name, int minimumBytes)
    {
        uint count = ReadUnsigned(reader);
        if (count > int.MaxValue || reader.BaseStream.CanSeek && (long)count * minimumBytes > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new MsyncException($"Native IR {name} pool exceeds remaining artifact bytes");
        return (int)count;
    }

    private static bool ReadCompactCode(CompactReader reader, NativeFunctionCode[] functions, SourceMapping[][] debug, SourceSpan[] spans)
    {
        for (int function = 0; function < functions.Length; function++)
            functions[function] = functions[function] with
            { Instructions = ReadCompactInstructions(reader, functions[function].Instructions.Count, debug[function], spans) };
        return true;
    }

    private static NativeInstruction[] ReadCompactInstructions(CompactReader reader, int count, SourceMapping[] debug, SourceSpan[] spans)
    {
        if (count > reader.Remaining) throw new MsyncException("Native IR instruction table exceeds remaining code section bytes");
        int nextSource = 0;
        if (reader.CanPreallocate)
        {
            NativeInstruction[] instructions = new NativeInstruction[count];
            for (int pc = 0; pc < instructions.Length; pc++) instructions[pc] = Read(pc);
            return instructions;
        }
        List<NativeInstruction> streamed = [];
        for (int pc = 0; pc < count; pc++) streamed.Add(Read(pc));
        return streamed.ToArray();

        NativeInstruction Read(int pc)
        {
            SourceSpan? source = nextSource < debug.Length && debug[nextSource].Pc == pc ? spans[debug[nextSource++].Span] : null;
            return ReadCompactInstruction(reader, source);
        }
    }

    private static (ModuleSemantics, ArtifactStage, int, int, bool, BindingContext?) ReadCompactHeader(CompactReader reader)
    {
        string language = reader.Text();
        if (language != LanguageVersion) throw new MsyncException($"Unsupported native IR language semantics '{language}'; expected '{LanguageVersion}'");
        ModuleSemantics semantics = (ModuleSemantics)reader.ReadByte(); ArtifactStage stage = (ArtifactStage)reader.ReadByte();
        int optimization = reader.ReadInt32(), entry = reader.ReadInt32(); bool preserve = reader.ReadBoolean();
        BindingContext? context = reader.ReadBoolean() ? new(reader.Text(), reader.Text(), ReadOptional(reader)) : null;
        return (semantics, stage, optimization, entry, preserve, context);
    }

    private static NativeFunctionCode ReadCompactFunction(BinaryReader input)
    {
        CompactReader reader = (CompactReader)input;
        string name = reader.Text();
        int parameters = reader.ReadInt32(), captures = reader.ReadInt32(), iterators = reader.ReadInt32();
        bool template = reader.ReadBoolean(); int instructionCount = reader.ReadInt32();
        if (instructionCount < 0) throw new MsyncException("Negative native IR instruction table length");
        NativeSlot[] slots = ReadArray(reader, value => new NativeSlot(value.ReadInt32(),
            (NativeSlotRole)value.ReadByte(), value.ReadBoolean(), ReadOptional(value)));
        NativeSignature signature = new(ReadArray(reader, value => value.ReadInt32()), reader.ReadInt32(),
            ReadArray(reader, value => new NativeCaptureContract(value.ReadInt32(), (NativeCaptureMode)value.ReadByte())));
        NativeFunctionReflectionSignature? reflection = reader.ReadBoolean() ? new(ReadArray(reader, ReadType), ReadType(reader)) : null;
        NativeStoragePlan storage = new(ReadArray(reader, value => (NativeSlotStorage)value.ReadByte()),
                ReadArray(reader, value => (NativeInstructionStorage)value.ReadByte()))
            {
                Calls = ReadArray(reader, value => new NativeCallStorage(value.ReadInt32(), value.ReadBoolean(),
                    ReadArray(value, item => item.ReadBoolean())))
            };
        return new(name, slots, new InstructionCount(instructionCount), signature, parameters, captures, iterators, template)
        { ReflectionSignature = reflection, Storage = storage };
    }

    private readonly record struct SourceMapping(int Pc, int Span);

    private static SourceMapping[][] ReadCompactDebug(CompactReader reader, IReadOnlyList<NativeFunctionCode> functions, SourceSpan[] spans)
    {
        SourceMapping[][] result = new SourceMapping[functions.Count][];
        for (int function = 0; function < functions.Count; function++)
        {
            int length = functions[function].Instructions.Count;
            uint count = reader.Unsigned();
            if (count > length || count > reader.Remaining) throw new MsyncException("Native IR debug mapping exceeds instruction table or section bytes");
            long previous = -1;
            if (reader.CanPreallocate)
            {
                SourceMapping[] sources = count == 0 ? [] : new SourceMapping[count];
                for (int entry = 0; entry < count; entry++) sources[entry] = ReadMapping();
                result[function] = sources;
            }
            else
            {
                List<SourceMapping> streamed = [];
                for (int entry = 0; entry < count; entry++) streamed.Add(ReadMapping());
                result[function] = streamed.ToArray();
            }

            SourceMapping ReadMapping()
            {
                uint delta = reader.Unsigned(); long pc = previous + delta;
                if (delta == 0 || pc >= length) throw new MsyncException("Invalid native IR debug instruction index");
                uint span = reader.Unsigned();
                if (span == 0 || span - 1 >= spans.Length) throw new MsyncException("Native IR debug mapping requires a valid source span");
                previous = pc;
                return new((int)pc, (int)span - 1);
            }
        }
        return result;
    }

    private sealed class InstructionCount(int count) : IReadOnlyList<NativeInstruction>
    {
        public int Count => count;
        public NativeInstruction this[int index] => throw new InvalidOperationException("Instruction section has not been read.");
        public IEnumerator<NativeInstruction> GetEnumerator() => throw new InvalidOperationException("Instruction section has not been read.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
