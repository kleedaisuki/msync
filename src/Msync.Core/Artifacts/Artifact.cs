using System.Text;

namespace Msync.Core;

public static partial class Artifact
{
    private static readonly byte[] Magic = "MSYNCBC\0"u8.ToArray();
    public const ushort FormatVersion = 5;
    public const string LanguageVersion = "0.8.0";
    public static Module Read(string path) => Read(path, formBlocks: true);

    public static Module Read(string path, bool formBlocks)
    {
        using var stream = File.OpenRead(path);
        try { return Read(stream, formBlocks); }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        { throw new MsyncException($"Cannot read bytecode artifact '{path}': {e.Message}", inner: e); }
    }
    public static Module Read(Stream stream) => Read(stream, formBlocks: true);

    public static Module Read(Stream stream, bool formBlocks)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new MsyncException("Not an msync binary bytecode artifact");
        var version = reader.ReadUInt16();
        if (version != FormatVersion) throw new MsyncException($"Unsupported msync bytecode format {version}; expected {FormatVersion}. Recompile the original .msync source with this version of msync.");
        var language = reader.ReadString();
        if (language != LanguageVersion) throw new MsyncException($"Unsupported bytecode language semantics {language}; expected {LanguageVersion}");
        var semantics = (ModuleSemantics)reader.ReadByte();
        if (semantics != ModuleSemantics.Current) throw new MsyncException($"Unsupported bytecode execution semantics {semantics}; recompile the original .msync source");
        var stage = (ArtifactStage)reader.ReadByte();
        if (!Enum.IsDefined(stage)) throw new MsyncException($"Unknown artifact stage {stage}");
        var optimization = reader.ReadInt32();
        var entry = reader.ReadInt32();
        BindingContext? context = reader.ReadBoolean() ? new(reader.ReadString(), reader.ReadString(), ReadOptional(reader)) : null;
        var constants = ReadArray(reader, ReadValue);
        var functions = ReadArray(reader, r =>
        {
            var name = r.ReadString(); var parameters = r.ReadInt32(); var locals = r.ReadInt32(); var captures = r.ReadInt32();
            var result = ReadType(r);
            var parameterTypes = r.ReadBoolean() ? ReadArray(r, ReadType) : null;
            var instructions = ReadArray(r, ir =>
            {
                var op = (OpCode)ir.ReadByte();
                if (!Enum.IsDefined(op)) throw new MsyncException($"Unknown bytecode opcode {(byte)op}");
                var a = ir.ReadInt32(); var b = ir.ReadInt32(); var text = ReadOptional(ir);
                SourceSpan? span = ir.ReadBoolean() ? new(ir.ReadString(), ir.ReadInt32(), ir.ReadInt32()) : null;
                return new Instruction(op, a, b, text, span);
            });
            return new FunctionCode(name, parameters, locals, instructions, result, captures, parameterTypes);
        });
        var plans = ReadArray(reader, ReadExecutionPlan);
        var module = new Module(constants, functions, entry, stage, context, optimization, Semantics: semantics)
        { ExecutionPlans = plans.Length == 0 ? null : plans };
        Validate(module);
        return formBlocks ? BlockBuilder.Form(module) : module;
    }
    public static void Write(Module module, string path)
    {
        ValidateExport(module);
        Validate(module);
        EncodePath(module, path);
    }

    /// <summary>Encode a compiler, binder, or reader-owned module without replaying validation.</summary>
    public static void WriteProduced(Module module, string path)
    {
        ValidateExport(module);
        EncodePath(module, path);
    }

    private static void EncodePath(Module module, string path)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.msync-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Encode(module, stream);
            File.Move(temp, full, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Write(Module module, Stream stream)
    {
        ValidateExport(module);
        Validate(module);
        Encode(module, stream);
    }

    public static void WriteProduced(Module module, Stream stream)
    {
        ValidateExport(module);
        Encode(module, stream);
    }

    private static void ValidateExport(Module module)
    {
        if (module.SessionDispatches is { Count: > 0 })
            throw new MsyncException("Open session call signatures require an active REPL session and cannot be exported as standalone bytecode. Compile a self-contained .msync source instead.");
    }

    private static void Encode(Module module, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic); writer.Write(FormatVersion); writer.Write(LanguageVersion); writer.Write((byte)module.Semantics); writer.Write((byte)module.Stage);
        writer.Write(module.OptimizationLevel); writer.Write(module.EntryPoint);
        writer.Write(module.Context is not null);
        if (module.Context is { } context) { writer.Write(context.WorkingDirectory); writer.Write(context.HomeDirectory); WriteOptional(writer, context.SourceFile); }
        WriteArray(writer, module.Constants, WriteValue);
        WriteArray(writer, module.Functions, (w, f) =>
        {
            w.Write(f.Name); w.Write(f.ParameterCount); w.Write(f.LocalCount); w.Write(f.CaptureCount); WriteType(w, f.ReturnType);
            w.Write(f.ParameterTypes is not null); if (f.ParameterTypes is not null) WriteArray(w, f.ParameterTypes, WriteType);
            WriteArray(w, f.Instructions, (iw, instruction) =>
            {
                iw.Write((byte)instruction.Op); iw.Write(instruction.A); iw.Write(instruction.B); WriteOptional(iw, instruction.Text);
                iw.Write(instruction.Span.HasValue);
                if (instruction.Span is { } span) { iw.Write(span.Source); iw.Write(span.Start); iw.Write(span.Length); }
            });
        });
        WriteArray(writer, module.ExecutionPlans ?? [], WriteExecutionPlan);
    }
    public static void Validate(Module module)
    {
        if (module.Semantics != ModuleSemantics.Current) throw new MsyncException($"Unsupported bytecode execution semantics {module.Semantics}; only the current execution contract is supported");
        if (module.EntryPoint < 0 || module.EntryPoint >= module.Functions.Count) throw new MsyncException("Bytecode entrypoint does not identify a function");
        if ((module.Stage == ArtifactStage.Executable) != (module.Context is not null)) throw new MsyncException("Bytecode stage and binding context disagree");
        ValidateSessionDispatches(module);
        ValidateExecutionPlans(module);
        foreach (var constant in module.Constants) ValidateConstant(constant, module);
        for (int functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
        {
            var function = module.Functions[functionIndex];
            if (function.ParameterCount < 0 || function.CaptureCount < 0 || function.LocalCount < function.ParameterCount + function.CaptureCount)
                throw new MsyncException($"Invalid local layout for function '{function.Name}'");
            if (function.ParameterTypes is { } parameterTypes && parameterTypes.Count != function.ParameterCount)
                throw new MsyncException($"Parameter type table disagrees with function '{function.Name}'");
            foreach (var instruction in function.Instructions)
            {
                if (instruction.Op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.IteratorNext && (instruction.A < 0 || instruction.A >= function.LocalCount))
                    throw new MsyncException($"Invalid local index {instruction.A} in '{function.Name}'", instruction.Span);
                if (instruction.Op == OpCode.Constant && (instruction.A < 0 || instruction.A >= module.Constants.Count))
                    throw new MsyncException($"Invalid constant index {instruction.A}", instruction.Span);
                if (instruction.Op is OpCode.ExecutePlan or OpCode.PlanResult && (module.ExecutionPlans is not { } executionPlans
                    || (uint)instruction.A >= (uint)executionPlans.Count || executionPlans[instruction.A].Function != functionIndex
                    || instruction.B < 0))
                    throw new MsyncException($"Invalid execution plan reference {instruction.A} in '{function.Name}'", instruction.Span);
                if (instruction.Op == OpCode.PlanResult && module.ExecutionPlans![instruction.A].ResultTransfers is null)
                    throw new MsyncException("An execution plan result requires an observed transfer", instruction.Span);
                if ((instruction.Op == OpCode.MakeClosure || instruction.Op == OpCode.SpecializeClosure && instruction.A >= 0)
                    && (instruction.A < 0 || instruction.A >= module.Functions.Count))
                    throw new MsyncException($"Invalid function index {instruction.A}", instruction.Span);
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue && (instruction.A < 0 || instruction.A > function.Instructions.Count))
                    throw new MsyncException($"Invalid jump target {instruction.A}", instruction.Span);
                if (instruction.Op == OpCode.IteratorNext && (instruction.B < 0 || instruction.B > function.Instructions.Count))
                    throw new MsyncException($"Invalid iterator target {instruction.B}", instruction.Span);
                if (instruction.Op == OpCode.Call && instruction.B < 0
                    && (module.SessionDispatches is not { } dispatches || (uint)(-instruction.B - 1) >= (uint)dispatches.Count))
                    throw new MsyncException("Open call signature requires its active session dispatch table", instruction.Span);
                if (instruction.Op == OpCode.SpecializeClosure && instruction.A < 0
                    && (module.SessionDispatches is not { } adaptations || (uint)(-instruction.A - 1) >= (uint)adaptations.Count))
                    throw new MsyncException("Open callback signature requires its active session dispatch table", instruction.Span);
                if (instruction.Op is OpCode.Call or OpCode.SpecializeClosure && instruction.Text is not null)
                {
                    foreach (var (source, target) in FunctionDispatch.Parse(instruction.Text))
                    {
                        if (source < 0 || source >= module.Functions.Count || target < 0 || target >= module.Functions.Count)
                            throw new MsyncException("Compiled dispatch table refers to a missing function", instruction.Span);
                        if (module.Functions[source].CaptureCount != module.Functions[target].CaptureCount)
                            throw new MsyncException("Compiled dispatch alternative changes closure capture layout", instruction.Span);
                        if (instruction.Op == OpCode.Call && module.Functions[target].ParameterCount != instruction.A)
                            throw new MsyncException("Compiled dispatch alternative changes call argument count", instruction.Span);
                    }
                }
            }
        }
        BytecodeValidation.Validate(module);
    }
    private static void ValidateSessionDispatches(Module module)
    {
        if (module.SessionDispatches is not { } tables) return;
        for (int slot = 0; slot < tables.Count; slot++)
        {
            var table = tables[slot];
            if (table.Slot != slot) throw new MsyncException("Session call signature slots must retain their dense identity order");
            foreach (var (identity, target) in table.Targets)
            {
                if ((uint)identity >= (uint)module.Functions.Count || (uint)target >= (uint)module.Functions.Count)
                    throw new MsyncException("Session call signature refers to a missing function");
                if (module.Functions[identity].CaptureCount != module.Functions[target].CaptureCount)
                    throw new MsyncException("Session call signature changes closure capture layout");
            }
        }
    }
    private static void ValidateConstant(Value value, Module module)
    {
        switch (value)
        {
            case FunctionValue { NativeName: not null } native:
                if (!NativeSignatures.TryGet(native.NativeName, out _)) throw new MsyncException($"Bytecode native function '{native.NativeName}' is not registered");
                break;
            case FunctionValue function:
                if (function.Function < 0 || function.Function >= module.Functions.Count) throw new MsyncException($"Bytecode function constant #{function.Function} does not identify a function");
                if ((function.Captures?.Count ?? 0) != module.Functions[function.Function].CaptureCount) throw new MsyncException("Bytecode function constant capture layout disagrees with function");
                foreach (var capture in function.Captures ?? []) ValidateConstant(capture, module);
                break;
            case ScopeValue scope:
                foreach (var member in scope.Members.Values) ValidateConstant(member, module);
                break;
            case ConnectionValue connection when !Enum.IsDefined(connection.Kind):
                throw new MsyncException($"Bytecode connection kind {(byte)connection.Kind} is not defined");
            case PathValue { Connection: not null } path: ValidateConstant(path.Connection, module); break;
            case RangeValue range:
                if (range.Resolve is not null || range.Selection is not null || range.SnapshotValues is null)
                    throw new MsyncException("Only explicit finite Range snapshots can be embedded in bytecode; lazy inputs must remain executable operations");
                foreach (Value item in range.SnapshotValues) ValidateConstant(item, module);
                break;
        }
    }
    private static T[] ReadArray<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        var count = reader.ReadInt32();
        if (count < 0) throw new MsyncException("Negative bytecode table length");
        if (reader.BaseStream.CanSeek && count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new MsyncException("Bytecode table length exceeds remaining artifact bytes");
        var items = new T[count]; for (var i = 0; i < count; i++) items[i] = read(reader); return items;
    }
    private static void WriteArray<T>(BinaryWriter writer, IReadOnlyList<T> values, Action<BinaryWriter, T> write)
    { writer.Write(values.Count); foreach (var value in values) write(writer, value); }
    private static string? ReadOptional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
    private static void WriteOptional(BinaryWriter writer, string? text) { writer.Write(text is not null); if (text is not null) writer.Write(text); }
    private static TypeRef ReadType(BinaryReader reader)
    { var kind = (TypeKind)reader.ReadUInt16(); return new(kind, reader.ReadBoolean() ? ReadType(reader) : null); }
    private static void WriteType(BinaryWriter writer, TypeRef type)
    { writer.Write((ushort)type.Kind); writer.Write(type.Element is not null); if (type.Element is not null) WriteType(writer, type.Element); }
    private static Value ReadValue(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => Value.Null,
        1 => new BoolValue(reader.ReadBoolean()),
        2 => new StringValue(reader.ReadString()),
        3 => new NumberValue(reader.ReadDouble()),
        4 => new PathValue(reader.ReadString(), ReadOptional(reader), reader.ReadBoolean() ? ReadConnection(reader) : null, ReadOptional(reader), ReadOptional(reader)),
        5 => ReadConnection(reader),
        6 => new TypeValue(ReadType(reader)),
        7 => new FunctionValue(reader.ReadInt32(), ReadArray(reader, ReadValue), ReadOptional(reader)),
        8 => new ScopeValue(ReadArray(reader, r => KeyValuePair.Create(r.ReadString(), ReadValue(r))).ToDictionary()),
        9 => ReadRangeSnapshot(reader),
        var tag => throw new MsyncException($"Unknown bytecode constant kind {tag}")
    };
    private static RangeValue ReadRangeSnapshot(BinaryReader reader)
    {
        TypeRef itemType = ReadType(reader);
        return new(ReadArray(reader, ReadValue), itemType);
    }
    private static ConnectionValue ReadConnection(BinaryReader reader) => new((ConnectionKind)reader.ReadByte(), reader.ReadString());
    private static void WriteValue(BinaryWriter writer, Value value)
    {
        switch (value)
        {
            case NullValue: writer.Write((byte)0); break;
            case BoolValue b: writer.Write((byte)1); writer.Write(b.Data); break;
            case StringValue s: writer.Write((byte)2); writer.Write(s.Data); break;
            case NumberValue n: writer.Write((byte)3); writer.Write(n.Data); break;
            case PathValue p:
                writer.Write((byte)4); writer.Write(p.Raw); WriteOptional(writer, p.WorkingDirectory); writer.Write(p.Connection is not null);
                if (p.Connection is not null) WriteConnection(writer, p.Connection); WriteOptional(writer, p.HomeDirectory); WriteOptional(writer, p.LocalSpelling); break;
            case ConnectionValue c: writer.Write((byte)5); WriteConnection(writer, c); break;
            case TypeValue t: writer.Write((byte)6); WriteType(writer, t.Data); break;
            case FunctionValue f: writer.Write((byte)7); writer.Write(f.Function); WriteArray(writer, f.Captures ?? [], WriteValue); WriteOptional(writer, f.NativeName); break;
            case ScopeValue s: writer.Write((byte)8); WriteArray(writer, s.Members.ToArray(), (w, pair) => { w.Write(pair.Key); WriteValue(w, pair.Value); }); break;
            case RangeValue { SnapshotValues: { } snapshot, Resolve: null, Selection: null } range:
                writer.Write((byte)9); WriteType(writer, range.ItemType); WriteArray(writer, snapshot, WriteValue); break;
            default: throw new MsyncException($"Runtime-owned {value.Type} values cannot be embedded in a bytecode constant table");
        }
    }
    private static void WriteConnection(BinaryWriter writer, ConnectionValue connection)
    { writer.Write((byte)connection.Kind); writer.Write(connection.Address); }
}
