namespace Msync.Optimization;

internal static class ScopeOperands
{
    internal static CodeLayout Construction(Module module, Instruction instruction) =>
        module.ScopeLayouts[instruction.Op == OpCode.NewScope
            ? instruction.A : module.StaticScopes[instruction.A].Layout];

    internal static CodeFieldProjection Projection(Module module, Instruction instruction) =>
        module.FieldProjections[instruction.A];

    internal static int Ordinal(Module module, Instruction instruction, int layout)
    {
        var projection = Projection(module, instruction);
        foreach (var alternative in projection.Cases)
            if (alternative.Layout == layout) return alternative.Ordinal;
        // An empty case table deliberately denotes a structural projection.
        if (projection.Cases.Count == 0)
        {
            var fields = module.ScopeLayouts[layout].Fields;
            for (int i = 0; i < fields.Count; i++)
                if (fields[i].Name == projection.Name) return i;
        }
        return -1;
    }

    internal static CodeField StaticField(Module module, Instruction instruction) =>
        module.ScopeLayouts[module.StaticScopes[instruction.A].Layout].Fields[instruction.B];

    internal static TypeRef Type(Module module, int id)
    {
        var type = module.CodeTypes[id];
        return new(type.Kind, type.Element < 0 ? null : Type(module, type.Element));
    }

    internal static TypeRef FieldType(Module module, Instruction instruction)
    {
        if (instruction.Op is OpCode.LoadStaticField or OpCode.StoreStaticField)
            return Type(module, StaticField(module, instruction).StorageType);
        var projection = Projection(module, instruction);
        TypeRef? result = null;
        foreach (var alternative in projection.Cases)
        {
            var type = Type(module, module.ScopeLayouts[alternative.Layout].Fields[alternative.Ordinal].StorageType);
            result = result is null || result == type ? type : new TypeRef(result.Kind | type.Kind);
        }
        return result ?? TypeRef.Any;
    }
}
