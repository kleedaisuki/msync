using System.Globalization;

namespace Msync.Core.Native;

public readonly partial struct NativeValue : IEquatable<NativeValue>
{
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
        return new(NativeKind.Range, root0: new() { Range = value });
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
    public string AsString() { Require(NativeKind.String); return root0.Text!; }
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
    public NativeRange AsRange() { Require(NativeKind.Range); return root0.Range!; }
    public NativeProcess AsProcess() { Require(NativeKind.Process); return root0.Process!; }
    public TypeRef AsType() { Require(NativeKind.Type); return root0.Type!; }
    public NativeAddress AsAddress()
    {
        Require(NativeKind.Address);
        return root0.Allocation!.AddressAt(Unpack(bits), root1.StorageType);
    }

    public TypeRef Type => Kind == NativeKind.Range ? root0.Range!.Type : TypeOfKind(Kind);

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
    internal static NativeValue Restore(NativeKind kind, ulong bits, byte flags, ReadOnlySpan<NativeRoot> roots) =>
        new(kind, bits, flags, roots.Length > 0 ? roots[0] : default,
            roots.Length > 1 ? roots[1] : default, roots.Length > 2 ? roots[2] : default,
            roots.Length > 3 ? roots[3] : default, roots.Length > 4 ? roots[4] : default);
    internal static ulong Pack(NativeOffset offset) => unchecked((uint)offset.Bytes | ((ulong)(uint)offset.References << 32));
    internal static NativeOffset Unpack(ulong bits) => new(unchecked((int)(uint)bits), unchecked((int)(uint)(bits >> 32)));

    public bool Equals(NativeValue other) => Kind == other.Kind && Kind switch
    {
        NativeKind.Null => true,
        NativeKind.Bool or NativeKind.Number => bits == other.bits,
        NativeKind.MachineInteger or NativeKind.MachineFloat => MachineEquals(other),
        NativeKind.String => root0.Text == other.root0.Text,
        NativeKind.Path => AsPath() == other.AsPath(),
        NativeKind.Connection => AsConnection() == other.AsConnection(),
        NativeKind.Function => AsFunction() == other.AsFunction(),
        NativeKind.Range => ReferenceEquals(root0.Range, other.root0.Range),
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
        NativeKind.String => HashCode.Combine(Kind, root0.Text),
        NativeKind.Path => HashCode.Combine(Kind, AsPath()),
        NativeKind.Connection => HashCode.Combine(Kind, AsConnection()),
        NativeKind.Function => HashCode.Combine(Kind, AsFunction()),
        NativeKind.Range => HashCode.Combine(Kind, root0.Range),
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
