using System.Numerics;

namespace Msync.Core.Native;

public sealed class NativeType : IEquatable<NativeType>
{
    private static readonly NativeKind[] SourceKinds = [NativeKind.Null, NativeKind.Bool, NativeKind.Number,
        NativeKind.String, NativeKind.Path, NativeKind.Connection, NativeKind.Function, NativeKind.Range,
        NativeKind.Process, NativeKind.Type, NativeKind.Address];
    private static readonly NativeType[] Builtins = SourceKinds
        .Select(kind => new NativeType(1UL << (int)kind)).ToArray();
    private readonly NativeRepresentation representation;
    private NativeType(ulong kinds)
    {
        Kinds = kinds;
        TypeKind source = TypeKind.Never;
        foreach (NativeKind kind in SourceKinds)
            if ((kinds & (1UL << (int)kind)) != 0) source |= NativeValue.TypeOfKind(kind).Kind;
        OptionalSourceType = BitOperations.IsPow2(kinds)
            ? NativeValue.TypeOfKind((NativeKind)BitOperations.TrailingZeroCount(kinds)) : new(source);
        representation = NativeRepresentation.Compute(this);
    }
    internal NativeType(NativeMachineType machine)
    {
        Machine = machine;
        Kinds = 1UL << (int)machine.Kind;
        representation = new(machine.ByteSize, 0, machine.Alignment);
    }
    public NativeMachineType? Machine { get; }
    public TypeRef? OptionalSourceType { get; }
    public bool IsMachine => Machine is not null;
    public static NativeType ForMachine(NativeMachineType machine) => machine.PhysicalType;
    internal NativeRepresentation Representation => representation;
    public static NativeType Never { get; } = new(0);
    public ulong Kinds { get; }
    public bool IsNever => Kinds == 0;
    public bool IsUnion => BitOperations.PopCount(Kinds) > 1;
    public NativeKind Kind => !IsUnion && !IsNever ? (NativeKind)BitOperations.TrailingZeroCount(Kinds)
        : throw new InvalidOperationException("The storage type has no single native kind.");
    public bool Accepts(NativeKind kind) => IsMachine ? kind == Kind
        : (uint)kind < Builtins.Length && (Kinds & (1UL << (int)kind)) != 0;
    public bool Accepts(NativeValue value) => IsMachine ? Machine!.HasSameValueType(value.MachineType) : Accepts(value.Kind);
    public bool Accepts(NativeType value) => IsMachine || value.IsMachine
        ? Machine?.HasSameValueType(value.Machine) == true : (value.Kinds & ~Kinds) == 0;
    public static NativeType Of(NativeKind kind) => (uint)kind < Builtins.Length
        ? Builtins[(int)kind] : throw new ArgumentOutOfRangeException(nameof(kind));
    public static NativeType FromTypeRef(TypeRef type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if ((type.Kind & ~TypeKind.Any) != 0)
            throw new ArgumentException($"{type} has no native language representation.", nameof(type));
        ulong kinds = 0;
        for (int index = 0; index < Builtins.Length; index++)
            if ((type.Kind & NativeValue.TypeOfKind((NativeKind)index).Kind) != 0) kinds |= 1UL << index;
        return FromKinds(kinds);
    }
    public static NativeType Union(params NativeType[] alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        if (alternatives.FirstOrDefault(alternative => alternative.IsMachine) is { } machine)
            return alternatives.All(alternative => alternative.Equals(machine)) ? machine
                : throw new ArgumentException("Machine SSA types do not form source-language unions.", nameof(alternatives));
        ulong kinds = 0;
        foreach (NativeType alternative in alternatives)
        {
            ArgumentNullException.ThrowIfNull(alternative);
            kinds |= alternative.Kinds;
        }
        return FromKinds(kinds);
    }
    internal static NativeType FromKinds(ulong kinds) => kinds == 0 ? Never
        : BitOperations.IsPow2(kinds) ? Of((NativeKind)BitOperations.TrailingZeroCount(kinds)) : new(kinds);
    public IEnumerable<NativeKind> Alternatives => IsMachine ? [Kind] : SourceKinds.Where(Accepts);
    public bool Equals(NativeType? other) => other is not null && Kinds == other.Kinds && ReferenceEquals(Machine, other.Machine);
    public override bool Equals(object? obj) => obj is NativeType other && Equals(other);
    public override int GetHashCode() => IsMachine ? HashCode.Combine(Kinds, Machine) : Kinds.GetHashCode();
    public override string ToString() => Machine?.ToString() ?? (IsNever ? "Never" : string.Join(" | ", Alternatives));
}

public readonly record struct NativeOffset(int Bytes, int References)
{
    public static NativeOffset operator +(NativeOffset left, NativeOffset right) =>
        new(checked(left.Bytes + right.Bytes), checked(left.References + right.References));
    public static NativeOffset operator *(NativeOffset offset, int count) =>
        new(checked(offset.Bytes * count), checked(offset.References * count));
}

public sealed record NativeField(string Name, NativeType Type);
public readonly record struct NativeFieldLayout(NativeType Type, NativeOffset Offset, string Name);

public sealed class NativeLayout
{
    internal NativeEnvironmentLayout? EnvironmentSchema { get; init; }
    private readonly IReadOnlyList<NativeFieldLayout> fields;
    private readonly NativeType? repeatedType;
    private readonly NativeLayout? repeatedLayout;
    private readonly NativeOffset stride;
    public NativeLayout(IEnumerable<NativeField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        NativeField[] declarations = fields.ToArray();
        NativeFieldLayout[] placed = new NativeFieldLayout[declarations.Length];
        int bytes = 0, references = 0, alignment = 1;
        for (int index = 0; index < declarations.Length; index++)
        {
            NativeField field = declarations[index];
            ArgumentNullException.ThrowIfNull(field.Type);
            ArgumentNullException.ThrowIfNull(field.Name);
            NativeRepresentation format = NativeRepresentation.Of(field.Type);
            HasCallableCells |= IsCallableCell(field.Type);
            bytes = Align(bytes, format.Alignment);
            placed[index] = new(field.Type, new(bytes, references), field.Name);
            bytes = checked(bytes + Math.Max(1, format.Bytes));
            references = checked(references + format.References);
            alignment = Math.Max(alignment, format.Alignment);
        }
        Alignment = alignment;
        RawSize = Math.Max(1, Align(bytes, alignment));
        ReferenceCount = references;
        this.fields = Fields = Array.AsReadOnly(placed);
    }
    private NativeLayout(NativeType type, int count)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        NativeRepresentation format = NativeRepresentation.Of(type);
        Alignment = format.Alignment;
        stride = new(Align(Math.Max(1, format.Bytes), Alignment), format.References);
        RawSize = checked(stride.Bytes * count);
        ReferenceCount = checked(stride.References * count);
        HasCallableCells = IsCallableCell(type);
        repeatedType = type;
        fields = Fields = new RepeatedFields(type, count, stride);
    }
    private NativeLayout(NativeLayout element, int count)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        repeatedLayout = element;
        Alignment = element.Alignment;
        stride = element.Extent;
        RawSize = checked(element.RawSize * count);
        ReferenceCount = checked(element.ReferenceCount * count);
        HasCallableCells = element.HasCallableCells;
        fields = Fields = new RepeatedLayoutFields(element, count);
    }
    public static NativeLayout Repeat(NativeLayout element, int count) => new(element, count);
    public static NativeLayout Repeat(NativeType type, int count) => new(type, count);
    public static NativeLayout Compose(IReadOnlyList<NativeLayout> parts, out IReadOnlyList<NativeOffset> offsets)
    {
        ArgumentNullException.ThrowIfNull(parts);
        NativeOffset[] bases = new NativeOffset[parts.Count];
        List<NativeFieldLayout> placed = [];
        int bytes = 0, references = 0, alignment = 1;
        for (int index = 0; index < parts.Count; index++)
        {
            NativeLayout part = parts[index];
            ArgumentNullException.ThrowIfNull(part);
            bytes = Align(bytes, part.Alignment);
            NativeOffset start = bases[index] = new(bytes, references);
            foreach (NativeFieldLayout field in part.Fields)
                placed.Add(field with { Offset = start + field.Offset });
            // Even a fieldless namespace has a distinct logical address.
            bytes = checked(bytes + Math.Max(1, part.RawSize));
            references = checked(references + part.ReferenceCount);
            alignment = Math.Max(alignment, part.Alignment);
        }
        offsets = Array.AsReadOnly(bases);
        return new(placed.ToArray(), Math.Max(1, Align(bytes, alignment)), references, alignment);
    }
    private NativeLayout(NativeFieldLayout[] fields, int bytes, int references, int alignment)
    {
        this.fields = Fields = Array.AsReadOnly(fields);
        RawSize = bytes;
        ReferenceCount = references;
        Alignment = alignment;
        HasCallableCells = fields.Any(static field => IsCallableCell(field.Type));
    }

    private NativeLayout(NativeLayout basis, int rawSize, int references, int alignment)
    {
        fields = Fields = basis.Fields;
        RawSize = rawSize;
        ReferenceCount = references;
        Alignment = alignment;
        HasCallableCells = basis.HasCallableCells;
    }

    internal NativeLayout AppendRaw(int bytes, int alignment, out int start)
    {
        NativeLayout result = AppendScratch(new(bytes, 0), alignment, out NativeOffset offset);
        start = offset.Bytes;
        return result;
    }

    internal NativeLayout WithoutWindowProducers(IReadOnlySet<int> producers, out int[] keep)
    {
        keep = Enumerable.Range(0, Count).Where(slot => !producers.Contains(slot))
            .OrderBy(slot => fields[slot].Offset.Bytes).ToArray();
        return new(keep.Select(index => new NativeField(fields[index].Name, fields[index].Type)));
    }

    internal NativeLayout WithParameterPrefix(int captures, int parameters)
    {
        int[] order = [.. Enumerable.Range(captures, parameters), .. Enumerable.Range(0, captures),
            .. Enumerable.Range(captures + parameters, Count - captures - parameters)];
        NativeLayout physical = new(order.Select(index => new NativeField(fields[index].Name, fields[index].Type)));
        NativeFieldLayout[] placed = new NativeFieldLayout[Count];
        for (int index = 0; index < order.Length; index++) placed[order[index]] = physical[index];
        return new(placed, physical.RawSize, physical.ReferenceCount, physical.Alignment);
    }

    internal NativeLayout AppendArgumentWindow(NativeLayout logicalRegisters, int[] keep, IReadOnlyList<int> arguments,
        IReadOnlySet<int> producers, out NativeOffset window, out NativeLayout parameters)
    {
        parameters = new(arguments.Select(index => new NativeField(logicalRegisters[index].Name, logicalRegisters[index].Type)));
        int alignment = Math.Max(Alignment, 16);
        // CALL publishes the callee's frame record immediately before the shared suffix.
        window = new(Align(checked(RawSize + NativeStackHeader.Bytes), alignment), ReferenceCount);
        NativeFieldLayout[] placed = new NativeFieldLayout[checked(logicalRegisters.Count + Count - keep.Length)];
        for (int index = 0; index < keep.Length; index++) placed[keep[index]] = fields[index];
        for (int index = keep.Length; index < Count; index++)
            placed[logicalRegisters.Count + index - keep.Length] = fields[index];
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!producers.Contains(arguments[index])) continue;
            NativeFieldLayout field = parameters[index];
            placed[arguments[index]] = field with { Offset = window + field.Offset };
        }
        return new(placed, Align(checked(window.Bytes + parameters.RawSize), alignment),
            checked(window.References + parameters.ReferenceCount), alignment);
    }

    // Hidden execution cells participate in storage/reset, not guest-addressable regions.
    internal NativeLayout AppendScratch(NativeOffset extent, int alignment, out NativeOffset start)
    {
        start = new(Align(RawSize, alignment), ReferenceCount);
        int combinedAlignment = Math.Max(Alignment, alignment);
        return new(this, Align(checked(start.Bytes + extent.Bytes), combinedAlignment),
            checked(start.References + extent.References), combinedAlignment);
    }

    public NativeLayout(params NativeType[] types)
        : this(types.Select((type, index) => new NativeField(index.ToString(System.Globalization.CultureInfo.InvariantCulture), type))) { }
    public IReadOnlyList<NativeFieldLayout> Fields { get; }
    public NativeFieldLayout this[int index] => fields[index];
    public int Count => fields.Count;
    public int RawSize { get; }
    public int ReferenceCount { get; }
    public int Alignment { get; }
    internal bool HasCallableCells { get; }
    private static bool IsCallableCell(NativeType type) => type.Kinds == 1UL << (int)NativeKind.Function;
    public NativeOffset Extent => new(RawSize, ReferenceCount);
    internal int Find(NativeOffset offset, NativeType? type = null)
    {
        if (repeatedType is not null && offset.Bytes >= 0 && offset.Bytes % stride.Bytes == 0)
        {
            int index = offset.Bytes / stride.Bytes;
            if (index < Count && offset == stride * index && (type is null || repeatedType.Equals(type))) return index;
        }
        if (repeatedLayout is { RawSize: > 0 } element && offset.Bytes >= 0)
        {
            int item = offset.Bytes / element.RawSize;
            if (item < Count / Math.Max(1, element.Count))
            {
                NativeOffset local = new(offset.Bytes % element.RawSize,
                    checked(offset.References - item * element.ReferenceCount));
                return checked(item * element.Count + element.Find(local, type));
            }
        }
        for (int index = 0; repeatedType is null && repeatedLayout is null && index < fields.Count; index++)
            if (fields[index].Offset == offset && (type is null || fields[index].Type.Equals(type))) return index;
        throw new ArgumentOutOfRangeException(nameof(offset), $"No typed field exists at {offset}.");
    }
    private sealed class RepeatedLayoutFields(NativeLayout element, int count) : IReadOnlyList<NativeFieldLayout>
    {
        public int Count { get; } = checked(element.Count * count);
        public NativeFieldLayout this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                NativeFieldLayout field = element[index % element.Count];
                return field with { Offset = field.Offset + element.Extent * (index / element.Count) };
            }
        }
        public IEnumerator<NativeFieldLayout> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class RepeatedFields(NativeType type, int count, NativeOffset stride) : IReadOnlyList<NativeFieldLayout>
    {
        public int Count => count;
        public NativeFieldLayout this[int index] => (uint)index < (uint)count
            ? new(type, stride * index, "") : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<NativeFieldLayout> GetEnumerator()
        {
            for (int index = 0; index < count; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal static int Align(int value, int alignment) => checked((value + alignment - 1) & -alignment);
}

internal readonly record struct NativeRepresentation(int Bytes, int References, int Alignment, int PayloadOffset = 0)
{
    internal static NativeRepresentation Of(NativeKind kind) => kind switch
    {
        NativeKind.Null => new(0, 0, 1),
        NativeKind.Bool => new(1, 0, 1),
        NativeKind.Number => new(8, 0, 8),
        NativeKind.String => new(0, 1, 1),
        NativeKind.Path => new(2, 5, 1),
        NativeKind.Connection => new(0, 1, 1),
        NativeKind.Function => new(4, 2, 4),
        NativeKind.Range or NativeKind.Process or NativeKind.Type => new(0, 1, 1),
        NativeKind.Address => new(8, 2, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    internal static NativeRepresentation Of(NativeType type) => type.Representation;
    internal static NativeRepresentation Compute(NativeType type)
    {
        if (type.IsNever) return new(0, 0, 1);
        if (!type.IsUnion) return Of(type.Kind);
        int bytes = 0, references = 0, alignment = 1;
        foreach (NativeKind kind in type.Alternatives)
        {
            NativeRepresentation alternative = Of(kind);
            bytes = Math.Max(bytes, alternative.Bytes);
            references = Math.Max(references, alternative.References);
            alignment = Math.Max(alignment, alternative.Alignment);
        }
        int payload = NativeLayout.Align(1, alignment);
        return new(checked(payload + bytes), references, alignment, payload);
    }
}
