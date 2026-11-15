using System.Collections.Concurrent;
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Core.Native;

public enum NativeMachineClass : byte { Integer, Floating }

public sealed class NativeMachineType
{
    private static readonly ConcurrentDictionary<(NativeMachineClass Class, uint Width,
        LlvmAPFloatSemantics Format, int Alignment), NativeMachineType> Types = new();

    private NativeMachineType(NativeMachineClass category, uint width, LlvmAPFloatSemantics format, int alignment)
    {
        Debug.Assert(width is > 0 and <= LlvmIntegerType.MaxIntBits && alignment > 0 && (alignment & (alignment - 1)) == 0);
        Class = category;
        BitWidth = width;
        FloatFormat = format;
        Alignment = alignment;
        AlignmentPayload = [alignment];
        ByteSize = checked((int)(((ulong)width + 7) / 8));
        PhysicalType = new(this);
    }

    public NativeMachineClass Class { get; }
    public uint BitWidth { get; }
    public LlvmAPFloatSemantics FloatFormat { get; }
    public int Alignment { get; }
    public int ByteSize { get; }
    internal IReadOnlyList<int> AlignmentPayload { get; }
    public bool IsInteger => Class == NativeMachineClass.Integer;
    public bool IsFloating => Class == NativeMachineClass.Floating;
    public NativeKind Kind => IsInteger ? NativeKind.MachineInteger : NativeKind.MachineFloat;
    public NativeType PhysicalType { get; }
    public bool HasSameValueType(NativeMachineType? other) => other is not null && Class == other.Class &&
        BitWidth == other.BitWidth && (!IsFloating || FloatFormat == other.FloatFormat);

    public static NativeMachineType Integer(uint bitWidth, int alignment = 1) =>
        Types.GetOrAdd((NativeMachineClass.Integer, bitWidth, default, alignment),
            static key => new(key.Class, key.Width, key.Format, key.Alignment));
    public static NativeMachineType Floating(LlvmAPFloatSemantics format, int alignment = 1) =>
        Types.GetOrAdd((NativeMachineClass.Floating, LlvmAPFloat.SemanticsSizeInBits(format), format, alignment),
            static key => new(key.Class, key.Width, key.Format, key.Alignment));

    public override string ToString() => IsInteger ? $"i{BitWidth} align {Alignment}"
        : $"{FloatFormat} align {Alignment}";
}

internal sealed class NativeWideScalar(NativeMachineType type, LlvmAPInt bits)
{
    internal NativeMachineType Type { get; } = type;
    internal LlvmAPInt Bits { get; } = bits;
}
