using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeConstantFolding
{
    internal static NativeConstant? Fold(NativeInstruction instruction, IReadOnlyDictionary<int, NativeConstant> known)
    {
        if (instruction.Op == NativeOp.MakeType)
        {
            if (instruction.A == -1) return new(NativeKind.Type, Type: new((TypeKind)instruction.Immediate));
            return known.TryGetValue(instruction.A, out NativeConstant? element) && element is { Kind: NativeKind.Type, Type: not null }
                ? new(NativeKind.Type, Type: new((TypeKind)instruction.Immediate, element.Type)) : null;
        }
        if (!known.TryGetValue(instruction.A, out NativeConstant? first)) return null;
        if (instruction.Op == NativeOp.Unary) return Unary(instruction, first, known);
        if (instruction.Op != NativeOp.Binary || !known.TryGetValue(instruction.B, out NativeConstant? second)) return null;
        NativeBinary operation = (NativeBinary)instruction.Immediate;
        if (first.Kind == NativeKind.Number && second.Kind == NativeKind.Number)
            return operation is >= NativeBinary.Add and <= NativeBinary.Power
                ? Number(NumberSemantics.Arithmetic(operation, first.Number, second.Number))
                : Compare(operation, NumberSemantics.Compare(first.Number, second.Number));
        if (first.Kind == NativeKind.String && second.Kind == NativeKind.String)
            return operation == NativeBinary.Add ? new(NativeKind.String, Text: first.Text + second.Text)
                : Compare(operation, TextSemantics.CompareOrdinal(first.Text!, second.Text!));
        if (first.Kind == NativeKind.Bool && second.Kind == NativeKind.Bool)
            return Compare(operation, first.Boolean.CompareTo(second.Boolean));
        if (first.Kind == NativeKind.Type && second.Kind == NativeKind.Type
            && operation is NativeBinary.Equal or NativeBinary.NotEqual)
            return Boolean((first.Type == second.Type) == (operation == NativeBinary.Equal));
        return null;
    }

    private static NativeConstant? Unary(NativeInstruction instruction, NativeConstant value,
        IReadOnlyDictionary<int, NativeConstant> known) => (NativeUnary)instruction.Immediate switch
        {
            NativeUnary.Not when value.Kind == NativeKind.Bool => Boolean(!value.Boolean),
            NativeUnary.IsNull => Boolean(value.Kind == NativeKind.Null),
            NativeUnary.ValueType => new(NativeKind.Type, Type: NativeValue.TypeOfKind(value.Kind)),
            NativeUnary.HasLayout when value.Kind != NativeKind.Address => Boolean(false),
            NativeUnary.Negate when value.Kind == NativeKind.Number => new(NativeKind.Number,
                Number: BitConverter.UInt64BitsToDouble(BitConverter.DoubleToUInt64Bits(value.Number) ^ (1UL << 63))),
            NativeUnary.IsType when known.TryGetValue(instruction.B, out NativeConstant? target)
                && target is { Kind: NativeKind.Type, Type: not null }
                => Boolean(target.Type.Accepts(value.Kind == NativeKind.Type ? value.Type! : NativeValue.TypeOfKind(value.Kind))),
            _ => null
        };

    private static NativeConstant? Compare(NativeBinary operation, int? order) =>
        operation is >= NativeBinary.Equal and <= NativeBinary.GreaterEqual ? Boolean(NumberSemantics.Compare(operation, order)) : null;

    private static NativeConstant Number(double value) => new(NativeKind.Number, Number: value);
    private static NativeConstant Boolean(bool value) => new(NativeKind.Bool, Boolean: value);
}
