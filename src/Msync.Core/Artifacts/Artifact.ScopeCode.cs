namespace Msync.Core;

public static partial class Artifact
{
    private sealed record ScopeCode(CodeType[] Types, CodeLayout[] Layouts, CodeStaticScope[] Statics,
        CodeFieldProjection[] Projections, TypeRef[] RuntimeTypes);

    private static ScopeCode ReadScopeCode(BinaryReader reader)
    {
        var types = ReadArray(reader, r => new CodeType((TypeKind)r.ReadUInt16(), r.ReadInt32(), ReadIds(r)));
        var layouts = ReadArray(reader, r => new CodeLayout(ReadArray(r, field => new CodeField(field.ReadString(), field.ReadInt32()))));
        var statics = ReadArray(reader, r => new CodeStaticScope(r.ReadInt32()));
        var projections = ReadArray(reader, r => new CodeFieldProjection(r.ReadString(),
            ReadArray(r, item => new CodeFieldCase(item.ReadInt32(), item.ReadInt32()))));
        return new(types, layouts, statics, projections, RuntimeCodeTypes(types));
    }

    private static void WriteScopeCode(BinaryWriter writer, Module module)
    {
        WriteArray(writer, module.CodeTypes, (w, type) =>
        {
            w.Write((ushort)type.Kind); w.Write(type.Element); WriteIds(w, type.ScopeLayouts);
        });
        WriteArray(writer, module.ScopeLayouts, (w, layout) =>
            WriteArray(w, layout.Fields, (field, value) => { field.Write(value.Name); field.Write(value.StorageType); }));
        WriteArray(writer, module.StaticScopes, (w, scope) => w.Write(scope.Layout));
        WriteArray(writer, module.FieldProjections, (w, projection) =>
        {
            w.Write(projection.Name);
            WriteArray(w, projection.Cases, (item, value) => { item.Write(value.Layout); item.Write(value.Ordinal); });
        });
    }

    private static int[] ReadIds(BinaryReader reader) => ReadArray(reader, static r => r.ReadInt32());
    private static void WriteIds(BinaryWriter writer, IReadOnlyList<int>? ids) =>
        WriteArray(writer, ids ?? [], static (w, id) => w.Write(id));

    private static CodeSignature ReadCodeSignature(BinaryReader reader) =>
        new(ReadIds(reader), reader.ReadInt32(),
            ReadArray(reader, r => new CodeCapture(r.ReadInt32(), r.ReadBoolean())), ReadIds(reader))
        { IsTemplate = reader.ReadBoolean() };

    private static void WriteCodeSignature(BinaryWriter writer, CodeSignature signature)
    {
        WriteIds(writer, signature.Parameters);
        writer.Write(signature.Result);
        WriteArray(writer, signature.Captures, (w, capture) => { w.Write(capture.Type); w.Write(capture.Location); });
        WriteIds(writer, signature.RequiredStatics);
        writer.Write(signature.IsTemplate);
    }

    internal static TypeRef[] RuntimeCodeTypes(IReadOnlyList<CodeType> types)
    {
        var result = new TypeRef[types.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var type = types[i];
            if ((type.Kind & ~TypeKind.Runtime) != 0 || type.Element < -1 || type.Element >= i
                || type.Element >= 0 && (type.Kind & (TypeKind.Range | TypeKind.Storage)) == 0)
                throw new MsyncException($"Invalid portable code type {i}");
            result[i] = new(type.Kind, type.Element < 0 ? null : result[type.Element]);
        }
        return result;
    }

    private static TypeRef RuntimeCodeType(int id, TypeRef[] types) =>
        (uint)id < (uint)types.Length ? types[id] : throw new MsyncException($"Invalid portable code type index {id}");

    private static bool Unrepresentable(OpCode op) =>
        op is OpCode.MakeScope or OpCode.GetMember or OpCode.SetMember or OpCode.GetSingleton or OpCode.SetSingleton;

    private static void ValidateScopeCode(Module module, TypeRef[] runtimeTypes)
    {
        for (int id = 0; id < module.CodeTypes.Count; id++)
        {
            var type = module.CodeTypes[id];
            var layouts = type.ScopeLayouts ?? [];
            if (layouts.Count != 0 && (type.Kind & TypeKind.Scope) == 0)
                throw new MsyncException($"Non-Scope code type {id} declares a layout domain");
            SortedIds(layouts, module.ScopeLayouts.Count, "code type layout");
        }
        for (int id = 0; id < module.ScopeLayouts.Count; id++)
        {
            var fields = module.ScopeLayouts[id].Fields;
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (field.Name.Length == 0 || !names.Add(field.Name))
                    throw new MsyncException($"Invalid or duplicate field name in portable layout {id}");
                _ = RuntimeCodeType(field.StorageType, runtimeTypes);
            }
        }
        foreach (var declaration in module.StaticScopes)
            RequireIndex(declaration.Layout, module.ScopeLayouts.Count, "static Scope layout");
        foreach (var projection in module.FieldProjections)
        {
            if (projection.Name.Length == 0) throw new MsyncException("A field projection requires a name");
            int previous = -1;
            foreach (var alternative in projection.Cases)
            {
                if (alternative.Layout <= previous)
                    throw new MsyncException($"Field projection '{projection.Name}' layouts must be sorted and unique");
                RequireIndex(alternative.Layout, module.ScopeLayouts.Count, "field projection layout");
                var fields = module.ScopeLayouts[alternative.Layout].Fields;
                RequireIndex(alternative.Ordinal, fields.Count, "field projection ordinal");
                if (fields[alternative.Ordinal].Name != projection.Name)
                    throw new MsyncException($"Field projection '{projection.Name}' does not match its selected ordinal");
                previous = alternative.Layout;
            }
        }
        foreach (var function in module.Functions)
        {
            var signature = function.CodeSignature
                ?? throw new MsyncException($"Function '{function.Name}' has no portable code signature");
            if (signature.Parameters.Count != function.ParameterCount || signature.Captures.Count != function.CaptureCount)
                throw new MsyncException($"Portable code signature disagrees with the frame of '{function.Name}'");
            if (RuntimeCodeType(signature.Result, runtimeTypes) != function.ReturnType)
                throw new MsyncException($"Portable code result disagrees with the type of '{function.Name}'");
            for (int i = 0; i < signature.Parameters.Count; i++)
            {
                var runtime = RuntimeCodeType(signature.Parameters[i], runtimeTypes);
                if (function.ParameterTypes is { } parameters && i < parameters.Count && parameters[i] != runtime)
                    throw new MsyncException($"Portable code parameter disagrees with the type of '{function.Name}'");
            }
            foreach (var capture in signature.Captures) _ = RuntimeCodeType(capture.Type, runtimeTypes);
            SortedIds(signature.RequiredStatics ?? [], module.StaticScopes.Count, "required static");
        }
        if (module.Functions[module.EntryPoint].CodeSignature is { RequiredStatics.Count: > 0 })
            throw new MsyncException("The program entry cannot require an initialized static Scope");
    }

    private static void ValidateScopeInstruction(Instruction instruction, Module module)
    {
        if (Unrepresentable(instruction.Op))
            throw new MsyncException($"Opcode {instruction.Op} does not belong to the portable layout format", instruction.Span);
        switch (instruction.Op)
        {
            case OpCode.NewScope:
                RequireIndex(instruction.A, module.ScopeLayouts.Count, "Scope construction layout");
                if (instruction.B < 0) throw new MsyncException("Negative Scope allocation site", instruction.Span);
                break;
            case OpCode.LoadField:
            case OpCode.StoreField:
                RequireIndex(instruction.A, module.FieldProjections.Count, "field projection");
                break;
            case OpCode.AdmitScope:
                RequireIndex(instruction.A, module.CodeTypes.Count, "Scope admission type");
                if (module.CodeTypes[instruction.A] is not { Kind: TypeKind.Scope, ScopeLayouts.Count: > 0 })
                    throw new MsyncException("Scope admission requires a finite non-null Scope domain", instruction.Span);
                break;
            case OpCode.LoadCell:
                RequireIndex(instruction.B - 1, module.CodeTypes.Count, "captured location type");
                break;
            case OpCode.JumpIfStaticReady:
                RequireIndex(instruction.B, module.StaticScopes.Count, "static Scope");
                break;
            case OpCode.LoadStaticScope:
            case OpCode.PublishStatic:
            case OpCode.LoadStaticField:
            case OpCode.StoreStaticField:
                RequireIndex(instruction.A, module.StaticScopes.Count, "static Scope");
                if (instruction.Op is OpCode.LoadStaticField or OpCode.StoreStaticField)
                    RequireIndex(instruction.B, module.ScopeLayouts[module.StaticScopes[instruction.A].Layout].Fields.Count,
                        "static field ordinal");
                break;
            default: return;
        }
        if (instruction.Text is not null)
            throw new MsyncException($"Unexpected text operand on {instruction.Op}", instruction.Span);
    }

    private static void RequireIndex(int id, int count, string name)
    {
        if ((uint)id >= (uint)count) throw new MsyncException($"Invalid {name} index {id}");
    }

    private static void SortedIds(IReadOnlyList<int> ids, int count, string name)
    {
        int previous = -1;
        foreach (int id in ids)
        {
            RequireIndex(id, count, name);
            if (id <= previous) throw new MsyncException($"{name} indices must be sorted and unique");
            previous = id;
        }
    }

    private static ScopeValue ReadScopeConstant(BinaryReader reader, IReadOnlyList<CodeLayout> layouts)
    {
        int id = reader.ReadInt32();
        RequireIndex(id, layouts.Count, "Scope constant layout");
        var fields = layouts[id].Fields;
        var values = new Value[fields.Count];
        for (int i = 0; i < values.Length; i++) values[i] = ReadValue(reader, layouts);
        return new(new ScopeLayout(fields.Select(field => field.Name), id), values);
    }

    private static void WriteScopeConstant(BinaryWriter writer, ScopeValue scope, IReadOnlyList<CodeLayout> layouts)
    {
        int id = scope.Layout.CodeLayoutId;
        RequireIndex(id, layouts.Count, "Scope constant layout");
        writer.Write(id);
        foreach (var field in layouts[id].Fields) WriteValue(writer, scope[field.Name], layouts);
    }
}
