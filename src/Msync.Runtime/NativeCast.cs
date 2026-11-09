using System.Globalization;
using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value CastOperation(IReadOnlyList<Value> arguments, RuntimeContext context) =>
        arguments[1] is TypeValue target ? CastValue(arguments[0], target.Data, context)
            : throw new MsyncException("cast expects a Type as its second argument.");

    private static Value CastValue(Value value, TypeRef target, RuntimeContext context)
    {
        if (value is NullValue)
            return target.Nullable ? value : throw CastFailure(value, target, "Handle Null first or admit Null in the target type.");
        if (value is RangeValue range && target.WithoutNull.Kind == TypeKind.Range)
            return target.Element is null || target.Element.Accepts(range.ItemType)
                ? range : CastRange(range, target.Element, context);
        if (target.Accepts(value.Type)) return value;
        return target.WithoutNull.Kind switch
        {
            TypeKind.String => value switch
            {
                PathValue path => new StringValue(path.NativeText),
                NumberValue or BoolValue or ConnectionValue or TypeValue => new StringValue(value.ToString()!),
                _ => throw CastFailure(value, target)
            },
            TypeKind.Path when value is StringValue => context.AsPath(value),
            TypeKind.Number => CastNumber(value),
            TypeKind.Bool => CastBoolean(value),
            TypeKind.Connection when value is StringValue text => CastConnection(text.Data),
            _ => throw CastFailure(value, target)
        };
    }

    private static NumberValue CastNumber(Value value)
    {
        if (value is BoolValue boolean) return new(boolean.Data ? 1 : 0);
        if (value is StringValue or PathValue
            && TryNumberText(Text(value), out double number)) return new(number);
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

    private static Value CastBoolean(Value value)
    {
        if (value is NumberValue number) return Bool(number.Data != 0 && !double.IsNaN(number.Data));
        if (value is StringValue or PathValue && bool.TryParse(Text(value), out bool boolean)) return Bool(boolean);
        throw CastFailure(value, TypeRef.Bool, "Use Number or the text 'true' or 'false'; strings are not tested for nonemptiness.");
    }

    private static ConnectionValue CastConnection(string text) => ExplicitConversions.ConnectionFromText(text)
        ?? throw CastFailure(new StringValue(text), TypeRef.Connection, "Use an explicit ssh:// or wsl:// endpoint, or a built-in endpoint name.");

    private static RangeValue CastRange(RangeValue source, TypeRef element, RuntimeContext context) =>
        new(CastItems(source, element, context), element, Resolve: source.Resolve is null ? null
            : async token => CastRange(await source.Resolve(token), element, context))
        { RetainedValues = source.RetainedValues };

    private static async IAsyncEnumerable<Value> CastItems(RangeValue source, TypeRef element, RuntimeContext context,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (Value value in source.Items.WithCancellation(token)) yield return CastValue(value, element, context);
    }

    private static MsyncException CastFailure(Value value, TypeRef target, string? help = null) =>
        new($"Cannot cast {value.Type} '{value}' to {target}.{(help is null ? "" : " " + help)}");
}
