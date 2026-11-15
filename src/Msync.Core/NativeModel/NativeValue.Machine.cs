using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Core.Native;

public readonly partial struct NativeValue
{
    public NativeMachineType? MachineType => Kind is NativeKind.MachineInteger or NativeKind.MachineFloat
        ? flags == 0 ? root0.MachineType : root0.WideScalar!.Type : null;
    public NativeType PhysicalType => MachineType?.PhysicalType ?? NativeType.Of(Kind);
    public TypeRef? OptionalSourceType => MachineType is null ? Type : null;

    public static NativeValue FromMachineInteger(LlvmAPInt bits, NativeMachineType? descriptor = null)
    {
        descriptor ??= NativeMachineType.Integer(bits.BitWidth);
        Debug.Assert(descriptor.IsInteger && descriptor.BitWidth == bits.BitWidth);
        return bits.IsSingleWord
            ? new(NativeKind.MachineInteger, bits.GetZExtValue(), root0: new() { MachineType = descriptor })
            : new(NativeKind.MachineInteger, flags: 1, root0: new() { WideScalar = new(descriptor, bits) });
    }

    public LlvmAPInt AsMachineInteger()
    {
        if (Kind == NativeKind.Bool) return new(1, bits);
        Require(NativeKind.MachineInteger);
        return flags == 0 ? new(root0.MachineType!.BitWidth, bits) : root0.WideScalar!.Bits;
    }

    public static NativeValue FromMachineFloat(LlvmAPFloat value, NativeMachineType? descriptor = null) =>
        FromMachineFloat(value.Semantics, value.BitcastToAPInt(), descriptor);
    public static NativeValue FromMachineFloat(LlvmAPFloatSemantics format, LlvmAPInt bits,
        NativeMachineType? descriptor = null)
    {
        descriptor ??= NativeMachineType.Floating(format);
        Debug.Assert(descriptor.IsFloating && descriptor.FloatFormat == format && descriptor.BitWidth == bits.BitWidth);
        return new(NativeKind.MachineFloat, bits.GetZExtValue(), root0: new() { MachineType = descriptor });
    }

    public LlvmAPFloat AsMachineFloat()
    {
        if (Kind == NativeKind.Number) return LlvmAPFloat.FromBits(bits);
        Require(NativeKind.MachineFloat);
        var machine = root0.MachineType!;
        return new(machine.FloatFormat, new(machine.BitWidth, bits));
    }

    internal LlvmAPInt MachineBits => Kind is NativeKind.MachineInteger or NativeKind.Bool ? AsMachineInteger()
        : AsMachineFloat().BitcastToAPInt();

    private bool MachineEquals(NativeValue other) => MachineType!.HasSameValueType(other.MachineType) &&
        (flags == 0 ? bits == other.bits : root0.WideScalar!.Bits == other.root0.WideScalar!.Bits);
    private int MachineHashCode()
    {
        var machine = MachineType!;
        return flags == 0 ? HashCode.Combine(Kind, machine.BitWidth, machine.FloatFormat, bits)
            : HashCode.Combine(Kind, machine.BitWidth, root0.WideScalar!.Bits);
    }
}
