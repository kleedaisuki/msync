using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

// Producer-owned target ABI facts. No SSA values are retained by this table.
public sealed class LlvmNativeMetadata(HirModule hir)
{
    private readonly List<NativeCodeType> types = [];
    private readonly List<NativeCodeLayout> layouts = [];
    private readonly Dictionary<(int Type, bool Nullable), int> mappedTypes = [];
    private readonly Dictionary<int, int> mappedLayouts = [];
    private readonly Dictionary<int, int> pointers = [];
    private readonly Dictionary<TypeRef, int> runtimeTypes = [];
    private readonly Dictionary<int, int> scopeTypes = [];
    private readonly Dictionary<LayoutKey, int> structuralLayouts = [];
    public IReadOnlyList<NativeCodeType> Types => types;
    public IReadOnlyList<NativeCodeLayout> Layouts => layouts;

    public int Type(HirTypeId id, bool admitsNull = false)
    {
        var key = (id.Value, admitsNull);
        if (mappedTypes.TryGetValue(key, out int index)) return index;
        index = types.Count;
        mappedTypes.Add(key, index);
        types.Add(new(TypeKind.Never));
        var type = hir.Types[id.Value];
        int element = type.Element >= 0 ? Type(new(type.Element))
            : type.RuntimeType.Element is { } nested ? RuntimeType(nested) : -1;
        int[]? admitted = null;
        if (type.ScopeDomain.Count != 0)
        {
            admitted = new int[type.ScopeDomain.Count];
            for (int layout = 0; layout < admitted.Length; ++layout)
                admitted[layout] = Layout(type.ScopeDomain[layout]);
        }
        types[index] = new(type.RuntimeType.Kind | (type.Nullable || admitsNull ? TypeKind.Null : TypeKind.Never), element, admitted);
        return index;
    }

    public int RuntimeType(TypeRef type)
    {
        if (runtimeTypes.TryGetValue(type, out int index)) return index;
        int element = type.Element is { } nested ? RuntimeType(nested) : -1;
        index = types.Count;
        runtimeTypes.Add(type, index);
        types.Add(new(type.Kind, element));
        return index;
    }

    public int Pointer(int pointee)
    {
        if (pointers.TryGetValue(pointee, out int index)) return index;
        index = types.Count;
        pointers.Add(pointee, index);
        types.Add(new(TypeKind.Never, Pointee: pointee));
        return index;
    }

    public int AddLayout(IReadOnlyList<NativeCodeField> fields)
    {
        var key = new LayoutKey(fields);
        if (structuralLayouts.TryGetValue(key, out int existing)) return existing;
        int index = layouts.Count;
        structuralLayouts.Add(key, index);
        layouts.Add(new(key.Fields));
        return index;
    }

    public int Scope(int layout)
    {
        if (scopeTypes.TryGetValue(layout, out int existing)) return existing;
        int index = types.Count;
        types.Add(new(TypeKind.Scope, Layouts: [layout]));
        scopeTypes.Add(layout, index);
        return index;
    }

    public int Layout(int shape)
    {
        if (mappedLayouts.TryGetValue(shape, out int index)) return index;
        index = layouts.Count;
        mappedLayouts.Add(shape, index);
        layouts.Add(new([]));
        layouts[index] = new(hir.ScopeLayouts[shape].Fields.Select(field =>
            new NativeCodeField(field.Name, Type(field.StorageType, field.AdmitsNull))).ToArray());
        structuralLayouts.TryAdd(new LayoutKey(layouts[index].Fields), index);
        return index;
    }

    internal LlvmNativeMetadata Fork(HirModule module)
    {
        var next = new LlvmNativeMetadata(module);
        next.types.AddRange(types);
        next.layouts.AddRange(layouts);
        foreach (var pair in mappedTypes) next.mappedTypes.Add(pair.Key, pair.Value);
        foreach (var pair in mappedLayouts) next.mappedLayouts.Add(pair.Key, pair.Value);
        foreach (var pair in pointers) next.pointers.Add(pair.Key, pair.Value);
        foreach (var pair in runtimeTypes) next.runtimeTypes.Add(pair.Key, pair.Value);
        foreach (var pair in scopeTypes) next.scopeTypes.Add(pair.Key, pair.Value);
        foreach (var pair in structuralLayouts) next.structuralLayouts.Add(pair.Key, pair.Value);
        return next;
    }

    private sealed class LayoutKey : IEquatable<LayoutKey>
    {
        internal readonly NativeCodeField[] Fields;
        internal LayoutKey(IReadOnlyList<NativeCodeField> fields) => Fields = fields.ToArray();
        public bool Equals(LayoutKey? other) => other is not null && Fields.AsSpan().SequenceEqual(other.Fields.AsSpan());
        public override bool Equals(object? value) => value is LayoutKey other && Equals(other);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var field in Fields) hash.Add(field);
            return hash.ToHashCode();
        }
    }
}
