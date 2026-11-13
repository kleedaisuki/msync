using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private NativeDatum Unary(NativeFrame frame, NativeInstruction instruction)
    {
        NativeDatum input = frame.GetDatum(instruction.A);
        NativeUnary operation = (NativeUnary)instruction.Immediate;
        if (operation == NativeUnary.ValueType) return new(NativeValue.FromType(input.LogicalType));
        if (operation == NativeUnary.HasLayout && !input.PhysicalType.Accepts(NativeKind.Address)) return new(NativeValue.False);
        NativeDatum type = operation == NativeUnary.IsType ? frame.GetDatum(instruction.B) : default;
        if (operation == NativeUnary.IsType && !input.IsReady && type.IsReady)
            return new(NativeValue.FromBool(type.Value.AsType().Accepts(input.LogicalType)));
        if (!input.IsReady || !type.IsReady) return PendingUnary(input, type, operation, instruction);
        return new(operation == NativeUnary.HasLayout
            ? NativeValue.FromBool(input.Value.Kind == NativeKind.Address && image.HasLayout(instruction.B, input.Value.AsAddress()))
            : UnaryValue(operation, input.Value, type.Value));
    }

    private NativeDatum PendingUnary(NativeDatum input, NativeDatum type, NativeUnary operation, NativeInstruction instruction)
    {
        if (operation == NativeUnary.HasLayout)
            return Track(input.Map(value => NativeValue.FromBool(value.Kind == NativeKind.Address
                && image.HasLayout(instruction.B, value.AsAddress())), instruction.Source, TypeRef.Bool));
        if (operation == NativeUnary.IsType)
            return Combine(input, type, (value, target) => UnaryValue(operation, value, target), instruction.Source, TypeRef.Bool);
        return Track(input.Map(value => UnaryValue(operation, value), instruction.Source,
            operation == NativeUnary.Negate ? TypeRef.Number : TypeRef.Bool));
    }

    internal static NativeValue UnaryValue(NativeUnary operation, NativeValue value, NativeValue type = default) => operation switch
    {
        NativeUnary.Not => NativeValue.FromBool(!value.AsBool()),
        NativeUnary.Negate => NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(
            BitConverter.DoubleToUInt64Bits(value.AsNumber()) ^ (1UL << 63))),
        NativeUnary.IsNull => NativeValue.FromBool(value.Kind == NativeKind.Null),
        NativeUnary.IsType => NativeValue.FromBool(type.AsType().Accepts(value.Kind == NativeKind.Type ? value.AsType() : value.Type)),
        NativeUnary.ValueType => NativeValue.FromType(value.Type),
        _ => throw new InvalidOperationException($"Native unary operation {operation} requires its instruction context.")
    };

    private NativeDatum PendingBinary(NativeDatum first, NativeDatum second, NativeInstruction instruction, NativeRuntimeContext? origin, TypeRef resultType) =>
        Combine(first, second,
            (left, right) => BinaryValue((NativeBinary)instruction.Immediate, left, right, origin), instruction.Source, resultType);

    private NativeDatum ConvertDatum(NativeDatum input, NativeInstruction instruction)
    {
        NativeRuntimeContext origin = context.CaptureDirectory();
        return input.IsReady ? new(image.Convert(input.Value, instruction.Type, instruction.Immediate != 0, origin))
            : PendingConvert(input, instruction, origin);
    }

    private NativeDatum PendingConvert(NativeDatum input, NativeInstruction instruction, NativeRuntimeContext origin) =>
        Track(input.Map(value => image.Convert(value, instruction.Type, instruction.Immediate != 0, origin), instruction.Source, image.SourceType(instruction.Type), image.Type(instruction.Type)));

    private NativeDatum NarrowDatum(NativeDatum input, NativeInstruction instruction, NativeType storage, string function, int pc) =>
        input.IsReady ? new(image.Narrow(input.Value, storage, instruction.Type, instruction.Source, function, pc))
            : PendingNarrow(input, instruction, storage, function, pc);

    private NativeDatum PendingNarrow(NativeDatum input, NativeInstruction instruction, NativeType storage, string function, int pc) =>
        Track(input.Map(value => image.Narrow(value, storage, instruction.Type, instruction.Source, function, pc), instruction.Source, image.SourceType(instruction.Type), storage, preserveIdentity: true));

    private NativeDatum MakeTypeDatum(NativeFrame frame, NativeInstruction instruction)
    {
        TypeKind kind = (TypeKind)instruction.Immediate;
        if (instruction.A < 0) return new(NativeValue.FromType(new(kind)));
        NativeDatum input = frame.GetDatum(instruction.A);
        return input.IsReady ? new(NativeValue.FromType(new(kind, input.Value.AsType())))
            : PendingMakeType(input, kind, instruction.Source);
    }

    private NativeDatum PendingMakeType(NativeDatum input, TypeKind kind, SourceSpan? source) =>
        Track(input.Map(value => NativeValue.FromType(new(kind, value.AsType())), source, TypeRef.Type));

    internal static NativeValue BinaryValue(NativeBinary operation, NativeValue left, NativeValue right, NativeRuntimeContext? origin)
    {
        if (operation == NativeBinary.PathJoin)
            return NativeValue.FromPath(NativeBuiltins.JoinPath(origin!.AsPath(left), right));
        if (operation == NativeBinary.Qualify)
            return NativeValue.FromPath(NativeBuiltins.QualifyPath(origin!.AsPath(right), left.AsConnection(), origin));
        if (operation == NativeBinary.RegexMatch)
            return NativeValue.FromBool(left.Kind is NativeKind.String or NativeKind.Path
                && Regex.IsMatch(ScalarText(left), ScalarText(right), RegexOptions.CultureInvariant));
        if (operation is >= NativeBinary.Equal and <= NativeBinary.GreaterEqual)
            return NativeValue.FromBool(Compare(operation, left, right));
        if (operation == NativeBinary.Add && left.Kind == NativeKind.String && right.Kind == NativeKind.String)
            return NativeValue.FromString(left.AsString() + right.AsString());
        return NativeValue.FromNumber(NumberSemantics.Arithmetic(operation, left.AsNumber(), right.AsNumber()));
    }

    private static bool Compare(NativeBinary operation, NativeValue left, NativeValue right)
    {
        if (operation is NativeBinary.Equal or NativeBinary.NotEqual
            && left.Kind == NativeKind.Function && right.Kind == NativeKind.Function)
            return (left.AsFunction() == right.AsFunction()) == (operation == NativeBinary.Equal);
        int? comparison = left.Kind == NativeKind.Number && right.Kind == NativeKind.Number
            ? NumberSemantics.Compare(left.AsNumber(), right.AsNumber())
            : left.Kind == NativeKind.Bool && right.Kind == NativeKind.Bool
                ? left.AsBool().CompareTo(right.AsBool())
                : left.Kind == NativeKind.Type && right.Kind == NativeKind.Type
                    ? left.AsType() == right.AsType() ? 0 : 1
                    : left.Kind is NativeKind.String or NativeKind.Path && right.Kind is NativeKind.String or NativeKind.Path
                        ? TextSemantics.CompareOrdinal(ScalarText(left), ScalarText(right))
                        : throw new MsyncException($"Cannot compare {left.Type} and {right.Type}; null supports only 'is' and 'is not'");
        return NumberSemantics.Compare(operation, comparison);
    }

    private static string ScalarText(NativeValue value) => value.Kind switch
    {
        NativeKind.String => value.AsString(),
        NativeKind.Path => value.AsPath().NativeText,
        NativeKind.Number or NativeKind.Bool => value.ToString(),
        _ => throw new MsyncException($"Cannot convert {value.Type} to String")
    };

    private NativeDatum CoerceStore(NativeDatum value, NativeType type)
    {
        if (value.PhysicalType.Equals(type)) return value;
        if (!type.IsUnion && !type.IsNever && type.Kind is NativeKind.Path or NativeKind.String)
        {
            NativeRuntimeContext origin = context.CaptureDirectory();
            value = value.IsReady ? new(CoerceStoredValue(value.Value, type.Kind, origin))
                : PendingStore(value, type.Kind, origin);
        }
        if ((value.PhysicalType.Kinds & ~type.Kinds) != 0)
            throw new MsyncException($"Cannot store {value.PhysicalType} in the actual physical location {type}.");
        return value;
    }

    private NativeDatum PendingStore(NativeDatum value, NativeKind kind, NativeRuntimeContext origin) =>
        Track(value.Map(item => CoerceStoredValue(item, kind, origin), resultType: kind == NativeKind.Path ? TypeRef.Path : TypeRef.String));

    private static NativeValue CoerceStoredValue(NativeValue value, NativeKind kind, NativeRuntimeContext origin)
    {
        NativeValue converted = kind == NativeKind.Path && value.Kind == NativeKind.String ? NativeValue.FromPath(origin.AsPath(value))
            : kind == NativeKind.String && value.Kind == NativeKind.Path ? NativeValue.FromString(value.AsPath().NativeText)
            : value;
        if (converted.Kind != kind)
            throw new MsyncException($"Cannot store {converted.Type} in the actual physical location {kind}.");
        return converted;
    }

    private NativeDatum Combine(NativeDatum left, NativeDatum right,
        Func<NativeValue, NativeValue, NativeValue> operation, SourceSpan? source, TypeRef? resultType = null)
    {
        if (left.IsReady && right.IsReady) return new(operation(left.Value, right.Value));
        List<NativeFuture> dependencies = [];
        if (left.Future is { } first) dependencies.Add(first);
        if (right.Future is { } second) dependencies.Add(second);
        return Track(new(new NativeFuture(EvaluateAsync(left, right, operation), dependencies, source: source, logicalType: resultType)));
    }

    private static async Task<NativeValue> EvaluateAsync(NativeDatum left, NativeDatum right,
        Func<NativeValue, NativeValue, NativeValue> operation) =>
        operation(await left.ResolveAsync(CancellationToken.None), await right.ResolveAsync(CancellationToken.None));

    private NativeDatum Track(NativeDatum value)
    {
        if (value.Future is { } future) (currentWork.Value ?? executionWork).Add(future);
        return value;
    }
}
