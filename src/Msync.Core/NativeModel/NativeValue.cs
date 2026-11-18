using System.Globalization;

namespace Msync.Core.Native;

public readonly partial struct NativeValue : IEquatable<NativeValue>
{
    private static readonly TypeRef StringRangeType = new(TypeKind.Range, TypeRef.String);
    private static readonly TypeRef NumberRangeType = new(TypeKind.Range, TypeRef.Number);
    private static readonly TypeRef RangeType = new(TypeKind.Range);
    private readonly ulong bits;
    private readonly byte flags;
    private readonly NativeRoot root0;
    private readonly NativeRoot root1;
    private readonly NativeRoot root2;
    private readonly NativeRoot root3;
    private readonly NativeRoot root4;

    private NativeValue(NativeKind kind, ulong bits = 0, byte flags = 0,
        NativeRoot root0 = default, NativeRoot root1 = default, NativeRoot root2 = default,
        NativeRoot root3 = default, NativeRoot root4 = default) =>
        (Kind, this.bits, this.flags, this.root0, this.root1, this.root2, this.root3, this.root4) =
        (kind, bits, flags, root0, root1, root2, root3, root4);

    public NativeKind Kind { get; }
    public static NativeValue Null => default;
    public static NativeValue True => FromBool(true);
    public static NativeValue False => FromBool(false);
    public static NativeValue FromBool(bool value) => new(NativeKind.Bool, value ? 1UL : 0);
    public static NativeValue FromNumber(double value) => new(NativeKind.Number, BitConverter.DoubleToUInt64Bits(value));
    public static NativeValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(NativeKind.String, root0: new() { Text = value });
    }
    public static NativeValue FromPath(NativePath value)
    {
        ArgumentNullException.ThrowIfNull(value.Raw);
        return new(NativeKind.Path, (ulong)(value.Connection?.Kind ?? ConnectionKind.Local),
            value.Connection.HasValue ? (byte)1 : (byte)0,
            new() { Text = value.Raw }, new() { Text = value.WorkingDirectory },
            new() { Text = value.HomeDirectory }, new() { Text = value.LocalSpelling },
            new() { Text = value.Connection?.Address });
    }
    public static NativeValue FromConnection(NativeConnection value) => FromConnectionResource(new(value));
    public static NativeValue FromConnectionResource(NativeConnectionResource value) =>
        new(NativeKind.Connection, root0: new() { Connection = value });
    public static NativeValue FromFunction(NativeFunction value) => new(NativeKind.Function,
        unchecked((uint)value.Function), root0: new() { Environment = value.Environment }, root1: new() { Text = value.NativeName });
    public static NativeValue FromRange(NativeRange value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.PlanEdge == 0 || value.ItemType != NativeRange.PlanItemType(value.PlanArena!, value.PlanEdge) ? new(NativeKind.Range, root0: new() { Range = value })
            : FromRangePlan(value.PlanArena!, value.PlanEdge);
    }
    public static NativeValue FromProcess(NativeProcess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(NativeKind.Process, root0: new() { Process = value });
    }
    public static NativeValue FromType(TypeRef value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(NativeKind.Type, root0: new() { Type = value });
    }
    public static NativeValue FromAddress(NativeAddress value)
    {
        if (value.IsNull) throw new ArgumentException("Null pointers are not namespace values.", nameof(value));
        return new(NativeKind.Address, Pack(value.Offset), root0: new() { Allocation = value.Allocation },
            root1: new() { StorageType = value.StorageType });
    }

    public bool AsBool() { Require(NativeKind.Bool); return bits != 0; }
    public double AsNumber() { Require(NativeKind.Number); return BitConverter.UInt64BitsToDouble(bits); }
    public string AsString()
    {
        Require(NativeKind.String);
        return bits == 0 ? root0.Text! : new string(NativeGuestObjectTable.TextSpan((nint)bits));
    }

    internal ReadOnlySpan<char> AsStringSpan()
    {
        Require(NativeKind.String);
        return bits == 0 ? root0.Text.AsSpan() : NativeGuestObjectTable.TextSpan((nint)bits);
    }

    internal NativeValue BuildString(ReadOnlySpan<char> first, ReadOnlySpan<char> second = default)
    {
        if (NativeStringEdge == 0) return FromString(string.Concat(first, second));
        var arena = (NativeGuestObjectTable)NativeStringArena!;
        nint edge = arena.AllocateTextBuffer!(checked(first.Length + second.Length)) | NativeGuestObjectTable.TextTag;
        Span<char> destination = NativeGuestObjectTable.TextBuffer(edge);
        first.CopyTo(destination);
        second.CopyTo(destination[first.Length..]);
        return FromTextEdge(arena, edge);
    }

    internal NativeValue OpenTextBuffer(int length, out Span<char> buffer)
    {
        var arena = (NativeGuestObjectTable)NativeStringArena!;
        nint edge = arena.AllocateTextBuffer!(length) | NativeGuestObjectTable.TextTag;
        buffer = NativeGuestObjectTable.TextBuffer(edge);
        return FromTextEdge(arena, edge);
    }

    internal static NativeValue FromTextEdge(NativeGuestObjectTable arena, nint edge) =>
        new(NativeKind.String, (ulong)edge, root0: new() { Value = arena });

    internal static NativeValue ConcatStrings(NativeValue left, NativeValue right) =>
        (left.NativeStringEdge != 0 ? left : right).BuildString(left.AsStringSpan(), right.AsStringSpan());

    internal nint NativeStringEdge => Kind == NativeKind.String ? (nint)bits : 0;
    internal object? NativeStringArena => root0.Value;
    public NativeConnection AsConnection() { Require(NativeKind.Connection); return root0.Connection!.Connection; }
    public NativePath AsPath()
    {
        Require(NativeKind.Path);
        NativeConnection? connection = flags == 0 ? null : new NativeConnection((ConnectionKind)bits, root4.Text ?? "");
        return new(root0.Text!, root1.Text, connection, root2.Text, root3.Text);
    }
    public NativeFunction AsFunction()
    {
        Require(NativeKind.Function);
        return new(unchecked((int)(uint)bits), root0.Environment, root1.Text);
    }
    internal static NativeValue FromRangePlan(NativeGuestObjectTable arena, nint edge) =>
        new(NativeKind.Range, (ulong)edge, root0: new() { Value = arena });
    internal nint RangePlan => Kind == NativeKind.Range ? (nint)bits : 0;
    internal NativeGuestObjectTable RangePlanArena => (NativeGuestObjectTable)root0.Value!;
    public NativeRange AsRange()
    {
        Require(NativeKind.Range);
        return bits == 0 ? root0.Range! : NativeRange.FromPlan(RangePlanArena, (nint)bits);
    }
    public NativeProcess AsProcess() { Require(NativeKind.Process); return root0.Process!; }
    public TypeRef AsType() { Require(NativeKind.Type); return root0.Type!; }
    public NativeAddress AsAddress()
    {
        Require(NativeKind.Address);
        return root0.Allocation!.AddressAt(Unpack(bits), root1.StorageType);
    }

    public TypeRef Type => Kind == NativeKind.Range ? (bits == 0 ? root0.Range!.Type : PlanRangeType(RangePlanArena, (nint)bits)) : TypeOfKind(Kind);

    private static TypeRef PlanRangeType(NativeGuestObjectTable arena, nint edge)
    {
        ref NativeRangeNode node = ref NativeRangeNode.At(edge);
        return node.Shape == 0 ? node.StringItems ? StringRangeType : NumberRangeType : arena.RangeShape(node.Shape).Range;
    }

    internal static TypeRef TypeOfKind(NativeKind kind) => kind switch
    {
        NativeKind.Null => TypeRef.Null,
        NativeKind.Bool => TypeRef.Bool,
        NativeKind.Number => TypeRef.Number,
        NativeKind.String => TypeRef.String,
        NativeKind.Path => TypeRef.Path,
        NativeKind.Connection => TypeRef.Connection,
        NativeKind.Function => TypeRef.Function,
        NativeKind.Range => RangeType,
        NativeKind.Process => TypeRef.Process,
        NativeKind.Type => TypeRef.Type,
        NativeKind.Address => TypeRef.Scope,
        _ => throw new InvalidOperationException("Unknown native kind.")
    };

    private void Require(NativeKind expected)
    {
        if (Kind != expected) throw new InvalidOperationException($"Expected {expected}, received {Kind}.");
    }
    internal ulong Bits => bits;
    internal byte Flags => flags;
    internal NativeRoot Root(int index) => index switch
    {
        0 => root0,
        1 => root1,
        2 => root2,
        3 => root3,
        4 => root4,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    internal static NativeValue Restore(NativeKind kind, ulong bits, byte flags, NativeRootBank roots, int start, int count)
    {
        if (kind == NativeKind.Range && roots.PlanEdge(start) is > 0 and var plan)
            return FromRangePlan(roots.ReferenceTable!, plan);
        if (kind == NativeKind.String && roots.TextEdge(start) is > 0 and var edge)
            return new(kind, (ulong)edge, root0: new() { Value = roots.ReferenceTable });
        return new(kind, bits, flags, kind switch
        {
            NativeKind.Address => new() { Allocation = roots.ReadAllocation(start) },
            NativeKind.Function => new() { Environment = roots.ReadEnvironment(start) },
            _ => count > 0 ? roots.ReadHost(start) : default,
        }, count > 1 ? roots.ReadHost(start + 1) : default, count > 2 ? roots.ReadHost(start + 2) : default,
            count > 3 ? roots.ReadHost(start + 3) : default, count > 4 ? roots.ReadHost(start + 4) : default);
    }
    internal static ulong Pack(NativeOffset offset) => unchecked((uint)offset.Bytes | ((ulong)(uint)offset.References << 32));
    internal static NativeOffset Unpack(ulong bits) => new(unchecked((int)(uint)bits), unchecked((int)(uint)(bits >> 32)));

    public bool Equals(NativeValue other) => Kind == other.Kind && Kind switch
    {
        NativeKind.Null => true,
        NativeKind.Bool or NativeKind.Number => bits == other.bits,
        NativeKind.MachineInteger or NativeKind.MachineFloat => MachineEquals(other),
        NativeKind.String => AsStringSpan().SequenceEqual(other.AsStringSpan()),
        NativeKind.Path => AsPath() == other.AsPath(),
        NativeKind.Connection => AsConnection() == other.AsConnection(),
        NativeKind.Function => AsFunction() == other.AsFunction(),
        NativeKind.Range => bits == other.bits && ReferenceEquals(root0.Value, other.root0.Value),
        NativeKind.Process => ReferenceEquals(root0.Process, other.root0.Process),
        NativeKind.Type => root0.Type == other.root0.Type,
        NativeKind.Address => ReferenceEquals(root0.Allocation, other.root0.Allocation) && bits == other.bits,
        _ => false
    };
    public override bool Equals(object? obj) => obj is NativeValue value && Equals(value);
    public override int GetHashCode() => Kind switch
    {
        NativeKind.Null => 0,
        NativeKind.Bool or NativeKind.Number => HashCode.Combine(Kind, bits),
        NativeKind.MachineInteger or NativeKind.MachineFloat => MachineHashCode(),
        NativeKind.String => HashCode.Combine(Kind, string.GetHashCode(AsStringSpan())),
        NativeKind.Path => HashCode.Combine(Kind, AsPath()),
        NativeKind.Connection => HashCode.Combine(Kind, AsConnection()),
        NativeKind.Function => HashCode.Combine(Kind, AsFunction()),
        NativeKind.Range => HashCode.Combine(Kind, bits, root0.Value),
        NativeKind.Process => HashCode.Combine(Kind, root0.Process),
        NativeKind.Type => HashCode.Combine(Kind, root0.Type),
        NativeKind.Address => HashCode.Combine(Kind, root0.Allocation, bits),
        _ => throw new InvalidOperationException("Unknown native kind.")
    };
    public override string ToString() => Kind switch
    {
        NativeKind.Null => "null",
        NativeKind.Bool => AsBool() ? "true" : "false",
        NativeKind.Number => AsNumber().ToString(CultureInfo.InvariantCulture),
        NativeKind.MachineInteger or NativeKind.MachineFloat => $"{MachineType}:0x{NativeMachineBits.Format(MachineBits)}",
        NativeKind.String => AsString(),
        NativeKind.Path => AsPath().ToString(),
        NativeKind.Connection => AsConnection().ToString(),
        NativeKind.Function => AsFunction().NativeName ?? $"fn #{AsFunction().Function}",
        NativeKind.Range => $"Range<{AsRange().ItemType}>",
        NativeKind.Process => $"Process(exit={AsProcess().ExitCode})",
        NativeKind.Type => AsType().ToString(),
        NativeKind.Address => $"address({AsAddress().Offset})",
        _ => throw new InvalidOperationException("Unknown native kind.")
    };
    public static implicit operator NativeValue(bool value) => FromBool(value);
    public static implicit operator NativeValue(double value) => FromNumber(value);
    public static implicit operator NativeValue(string value) => FromString(value);
    public static implicit operator NativeValue(NativePath value) => FromPath(value);
    public static implicit operator NativeValue(NativeConnection value) => FromConnection(value);
    public static implicit operator NativeValue(NativeFunction value) => FromFunction(value);
    public static implicit operator NativeValue(NativeRange value) => FromRange(value);
    public static implicit operator NativeValue(NativeProcess value) => FromProcess(value);
    public static implicit operator NativeValue(TypeRef value) => FromType(value);
    public static implicit operator NativeValue(NativeAddress value) => FromAddress(value);
    public static bool operator ==(NativeValue left, NativeValue right) => left.Equals(right);
    public static bool operator !=(NativeValue left, NativeValue right) => !left.Equals(right);
}
