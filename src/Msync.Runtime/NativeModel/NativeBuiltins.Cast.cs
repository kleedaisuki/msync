using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    public static NativeValue ConvertValue(NativeValue value, TypeRef target, NativeRuntimeContext context) =>
        CastValue(value, target, context);

    private static NativeValue CastValue(NativeValue value, TypeRef target, NativeRuntimeContext context)
    {
        if (value.Kind == NativeKind.Null)
            return target.Nullable ? value : throw CastFailure(value, target, "Handle Null first or admit Null in the target type.");
        if (value.Kind == NativeKind.Range && target.WithoutNull.Kind == TypeKind.Range)
        {
            NativeRange range = value.AsRange();
            return target.Element is null || target.Element.Accepts(range.ItemType) ? value
                : NativeValue.FromRange(new(CastItems(range, target.Element, context.CaptureDirectory()), target.Element, [value]));
        }
        if (target.Accepts(value.Type)) return value;
        return target.WithoutNull.Kind switch
        {
            TypeKind.String when value.Kind == NativeKind.Path => NativeValue.FromString(value.AsPath().NativeText),
            TypeKind.String when value.Kind is NativeKind.Number or NativeKind.Bool or NativeKind.Connection or NativeKind.Type =>
                NativeValue.FromString(value.ToString()),
            TypeKind.Path when value.Kind == NativeKind.String => NativeValue.FromPath(context.AsPath(value)),
            TypeKind.Number => CastNumber(value),
            TypeKind.Bool => CastBoolean(value),
            TypeKind.Connection when value.Kind == NativeKind.String => NativeValue.FromConnection(ConnectionFromText(value.AsString())
                ?? throw CastFailure(value, target, "Use an explicit ssh:// or wsl:// endpoint, or a built-in endpoint name.")),
            _ => throw CastFailure(value, target)
        };
    }

    private static NativeValue CastNumber(NativeValue value)
    {
        if (value.Kind == NativeKind.Bool) return NativeValue.FromNumber(value.AsBool() ? 1 : 0);
        if (value.Kind is NativeKind.String or NativeKind.Path && TryNumberText(Text(value), out double number))
            return NativeValue.FromNumber(number);
        throw CastFailure(value, TypeRef.Number, "Use invariant numeric text, such as '12.5' or '1e3'.");
    }

    private static bool TryNumberText(string text, out double number)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return true;
        string literal = text.Trim();
        bool negative = literal.StartsWith('-');
        if (literal.StartsWith('+') || negative) literal = literal[1..];
        if (!NumberSyntax.TryParse(literal, out number)) return false;
        if (negative) number = -number;
        return true;
    }

    private static NativeValue CastBoolean(NativeValue value)
    {
        if (value.Kind == NativeKind.Number)
        {
            double number = value.AsNumber();
            return Bool(number != 0 && !double.IsNaN(number));
        }
        if (value.Kind is NativeKind.String or NativeKind.Path && bool.TryParse(Text(value), out bool boolean)) return Bool(boolean);
        throw CastFailure(value, TypeRef.Bool, "Use Number or the text 'true' or 'false'; strings are not tested for nonemptiness.");
    }

    internal static NativeConnection? ConnectionFromText(string text) => ExplicitConversions.ConnectionFromText(text);

    private static async IAsyncEnumerable<NativeValue> CastItems(NativeRange source, TypeRef element,
        NativeRuntimeContext context, [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (NativeValue value in source.Items.WithCancellation(token)) yield return CastValue(value, element, context);
    }

    private static MsyncException CastFailure(NativeValue value, TypeRef target, string? help = null) =>
        new($"Cannot cast {value.Type} '{value}' to {target}.{(help is null ? "" : " " + help)}");
}
