using System.Diagnostics;
using System.Globalization;
using Msync.Core.Llvm;

namespace Msync.Core.Native;

public sealed partial record NativeCodeType
{
    internal const ushort IntegerWireTag = 0x1000;
    internal const ushort FloatingWireTag = 0x1001;
    public NativeMachineType? Machine { get; init; }
    public bool IsMachine => Machine is not null;
    public static NativeCodeType ForMachine(NativeMachineType descriptor) => new(TypeKind.Never) { Machine = descriptor };
    public NativeType PhysicalType => Machine?.PhysicalType ?? (Pointee >= 0
        ? NativeType.Of(NativeKind.Address) : NativeType.FromTypeRef(new(Kind)));

    internal ushort WireKind => Machine is { } machine ? machine.IsInteger ? IntegerWireTag : FloatingWireTag : (ushort)Kind;
    internal int WireElement => Machine is { } machine ? machine.IsInteger ? (int)machine.BitWidth : (int)machine.FloatFormat : Element;
    internal IReadOnlyList<int>? WireLayouts => Machine?.AlignmentPayload ?? Layouts;

    internal static NativeCodeType FromWire(ushort kind, int element, IReadOnlyList<int>? layouts, int pointee)
    {
        if (kind is not (IntegerWireTag or FloatingWireTag)) return new((TypeKind)kind, element, layouts, pointee);
        if (pointee != -1 || layouts is not { Count: 1 } || layouts[0] <= 0 || (layouts[0] & (layouts[0] - 1)) != 0)
            throw new MsyncException("Invalid native machine-scalar type record");
        NativeMachineType machine;
        if (kind == IntegerWireTag)
        {
            if (element <= 0 || element > 1 << 23) throw new MsyncException("Invalid native machine-integer width");
            machine = NativeMachineType.Integer((uint)element, layouts[0]);
        }
        else
        {
            if ((uint)element > (uint)LlvmAPFloatSemantics.IEEEdouble) throw new MsyncException("Unknown native machine-floating format");
            machine = NativeMachineType.Floating((LlvmAPFloatSemantics)element, layouts[0]);
        }
        return ForMachine(machine);
    }
}

public sealed partial record NativeConstant
{
    public static NativeConstant FromMachineInteger(LlvmAPInt bits, NativeMachineType? descriptor = null)
    {
        descriptor ??= NativeMachineType.Integer(bits.BitWidth);
        Debug.Assert(descriptor.IsInteger && descriptor.BitWidth == bits.BitWidth);
        return new(NativeKind.MachineInteger, FormattableString.Invariant($"{bits.BitWidth}:{descriptor.Alignment}:{NativeMachineBits.Format(bits)}"));
    }
    public static NativeConstant FromMachineFloat(LlvmAPFloat value, NativeMachineType? descriptor = null)
    {
        descriptor ??= NativeMachineType.Floating(value.Semantics);
        Debug.Assert(descriptor.IsFloating && descriptor.FloatFormat == value.Semantics);
        return new(NativeKind.MachineFloat, FormattableString.Invariant($"{(byte)value.Semantics}:{descriptor.Alignment}:{NativeMachineBits.Format(value.BitcastToAPInt())}"));
    }
    public NativeValue ToMachineValue()
    {
        NativeMachineType machine = ParseMachineType(out ReadOnlySpan<char> bitString);
        LlvmAPInt bits = NativeMachineBits.Parse(machine.BitWidth, bitString);
        return machine.IsInteger ? NativeValue.FromMachineInteger(bits, machine)
            : NativeValue.FromMachineFloat(machine.FloatFormat, bits, machine);
    }
    public NativeMachineType? MachineType => Kind is NativeKind.MachineInteger or NativeKind.MachineFloat
        ? ParseMachineType(out _) : null;

    private NativeMachineType ParseMachineType(out ReadOnlySpan<char> bitString)
    {
        if (Kind is not (NativeKind.MachineInteger or NativeKind.MachineFloat) || Text is null)
            throw new MsyncException("Missing native machine-scalar literal payload");
        ReadOnlySpan<char> text = Text;
        int first = text.IndexOf(':');
        int second = first < 0 ? -1 : text[(first + 1)..].IndexOf(':') + first + 1;
        if (first < 1 || second <= first + 1 ||
            !uint.TryParse(text[..first], NumberStyles.None, CultureInfo.InvariantCulture, out uint parameter) ||
            !int.TryParse(text[(first + 1)..second], NumberStyles.None, CultureInfo.InvariantCulture, out int alignment) ||
            alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new MsyncException("Invalid native machine-scalar literal descriptor");
        NativeMachineType machine;
        if (Kind == NativeKind.MachineInteger)
        {
            if (parameter == 0 || parameter > LlvmIntegerType.MaxIntBits) throw new MsyncException("Invalid native machine-integer literal width");
            machine = NativeMachineType.Integer(parameter, alignment);
        }
        else
        {
            if (parameter > (byte)LlvmAPFloatSemantics.IEEEdouble) throw new MsyncException("Unknown native machine-floating literal format");
            machine = NativeMachineType.Floating((LlvmAPFloatSemantics)parameter, alignment);
        }
        bitString = text[(second + 1)..];
        return machine;
    }
}

internal static class NativeMachineBits
{
    internal static string Format(LlvmAPInt bits)
    {
        const string digits = "0123456789ABCDEF";
        int count = checked((int)(((ulong)bits.BitWidth + 3) / 4));
        return string.Create(count, bits, static (text, bits) =>
        {
            for (int index = 0; index < text.Length; ++index)
            {
                int bit = (text.Length - index - 1) * 4;
                text[index] = digits[(int)((bits.GetWord((uint)(bit / 64)) >> (bit % 64)) & 15)];
            }
        });
    }
    internal static LlvmAPInt Parse(uint width, ReadOnlySpan<char> text)
    {
        if (text.Length != (int)(((ulong)width + 3) / 4)) throw new MsyncException("Invalid native machine-scalar literal bit extent");
        Span<ulong> words = width <= 64 ? stackalloc ulong[1] : new ulong[(width + 63) / 64];
        words.Clear();
        for (int index = 0; index < text.Length; ++index)
        {
            int digit = text[index] switch { >= '0' and <= '9' => text[index] - '0', >= 'A' and <= 'F' => text[index] - 'A' + 10, _ => -1 };
            if (digit < 0 || index == 0 && width % 4 != 0 && digit >= 1 << (int)(width % 4))
                throw new MsyncException("Invalid native machine-scalar literal hexadecimal bits");
            int bit = (text.Length - index - 1) * 4;
            words[bit / 64] |= (ulong)digit << (bit % 64);
        }
        return new(width, words);
    }
}
