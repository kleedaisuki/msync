using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    public static ValueTask<NativeRange> DemandRangeAsync(NativeRange range, CancellationToken token = default) =>
        NativeRangeDemand.DemandAsync(range, token);

    private static async ValueTask<NativeValue> RangeOperationAsync(string operation, IReadOnlyList<NativeValue> args,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke, CancellationToken token)
    {
        if (operation == "of") return NativeValue.FromRange(NativeRange.Snapshot(args, ElementType(args)));
        if (operation == "single") return NativeValue.FromRange(NativeRange.Snapshot([args[0]], args[0].Type));
        if (operation == "empty_of") return NativeValue.FromRange(NativeRange.Snapshot([], args[0].AsType()));
        if (operation == "less") return Bool(CompareRangeValues(args[0], args[1]) < 0);
        if (operation == "repeat") return NativeValue.FromRange(new(Repeated(args[0], Ordinal(args[1], operation, false), token), args[0].Type, [args[0]]));
        if (operation == "sequence")
        {
            double first = FiniteNumber(args[0], operation), last = FiniteNumber(args[1], operation), step = FiniteNumber(args[2], operation);
            if (step == 0) throw new MsyncException("ranges.sequence requires a nonzero step");
            return NativeValue.FromRange(new(Sequence(first, last, step, token), TypeRef.Number));
        }
        NativeRange source = Range(args[0], operation);
        if (operation is "identity" or "all") return args[0];
        if (operation is "filter" or "transform" or "take_while" or "drop_while")
            return NativeValue.FromRange(Adapt(source, operation == "transform" ? TypeRef.Any : source.ItemType,
                (range, cancellation) => CallbackRange(range, operation, args[1], invoke, cancellation), token, [args[1]],
                operation == "transform" ? NativeType.FromTypeRef(TypeRef.Any) : source.PhysicalItemType));
        if (operation is "take" or "drop" or "stride" or "step")
        {
            long count = Ordinal(args[1], operation, operation is "stride" or "step");
            if (operation == "take" && count == 0) return NativeValue.FromRange(NativeRange.Snapshot([], source.ItemType, source.PhysicalItemType));
            return NativeValue.FromRange(Adapt(source, source.ItemType,
                (range, cancellation) => CountedRange(range, operation, count, cancellation), token));
        }
        if (operation is "reverse" or "unique")
            return NativeValue.FromRange(Adapt(source, source.ItemType,
                (range, cancellation) => OrderedRange(range, operation, cancellation), token));
        if (IsExtendedRangeOperation(operation)) return await ExtendedRangeOperationAsync(operation, args, invoke, token);
        return await ConsumeRangeAsync(source, operation, args, invoke, token);
    }

    private static TypeRef ElementType(IEnumerable<NativeValue> values) => values.Aggregate(
        new TypeRef(TypeKind.Never), static (type, value) => TypeRef.Union(type, value.Type));

    private static NativeRange Adapt(NativeRange source, TypeRef itemType,
        Func<NativeRange, CancellationToken, IAsyncEnumerable<NativeValue>> operation,
        CancellationToken token, IReadOnlyList<NativeValue>? captures = null, NativeType? physicalItemType = null)
    {
        physicalItemType ??= source.PhysicalItemType;
        NativePathSelection? selection = source.Selection;
        NativePathSelection? adapted = selection is null ? null : selection with
        {
            Apply = (items, cancellation) => operation(new NativeRange(
                selection.Apply is null ? items : selection.Apply(items, cancellation), source.ItemType, physicalItemType: source.PhysicalItemType), cancellation)
        };
        NativeValue[] roots = [NativeValue.FromRange(source), .. captures ?? []];
        return new(operation(source, token), itemType, roots, adapted,
            source.Resolve is null ? null : async cancellation => Adapt(await source.Resolve(cancellation), itemType, operation, cancellation, captures, physicalItemType),
            physicalItemType: physicalItemType);
    }

    private static async IAsyncEnumerable<NativeValue> Sequence(double first, double last, double step,
        [EnumeratorCancellation] CancellationToken token)
    {
        for (double value = first; step > 0 ? value < last : value > last;)
        {
            token.ThrowIfCancellationRequested();
            yield return NativeValue.FromNumber(value);
            double next = value + step;
            if (next == value) throw new MsyncException("ranges.sequence step no longer advances the Number value");
            value = next;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<NativeValue> Repeated(NativeValue value, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        for (long index = 0; index < count; index++) { token.ThrowIfCancellationRequested(); yield return value; }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<NativeValue> CountedRange(NativeRange source, string operation, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (operation == "take" && count == 0) yield break;
        long remaining = count;
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            if (operation == "take") { yield return value; if (--remaining == 0) yield break; continue; }
            if (operation == "drop") { if (remaining == 0) yield return value; else --remaining; continue; }
            if (remaining == count) yield return value;
            if (--remaining == 0) remaining = count;
        }
    }

    private static async IAsyncEnumerable<NativeValue> CallbackRange(NativeRange source, string operation, NativeValue callback,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        bool dropping = operation == "drop_while";
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            if (operation == "drop_while" && !dropping) { yield return value; continue; }
            NativeValue result = await invoke(callback, [value], token);
            if (operation == "transform") { yield return result; continue; }
            bool selected = Boolean(result);
            if (operation == "take_while" && !selected) yield break;
            if (operation == "drop_while") { dropping = selected; if (!selected) yield return value; continue; }
            if (selected) yield return value;
        }
    }

    private static async IAsyncEnumerable<NativeValue> OrderedRange(NativeRange source, string operation,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (operation == "unique")
        {
            HashSet<NativeValue> seen = new(NativeLanguageEquality.Instance);
            await foreach (NativeValue value in source.Items.WithCancellation(token)) if (seen.Add(value)) yield return value;
            yield break;
        }
        List<NativeValue> values = [];
        await foreach (NativeValue value in source.Items.WithCancellation(token)) values.Add(value);
        for (int index = values.Count - 1; index >= 0; index--) { token.ThrowIfCancellationRequested(); yield return values[index]; }
    }

    private static async ValueTask<NativeValue> ConsumeRangeAsync(NativeRange source, string operation, IReadOnlyList<NativeValue> args,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke, CancellationToken token)
    {
        NativeValue? last = null;
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            if (operation == "empty") return NativeValue.False;
            if (operation is "front" or "first") return value;
            if (operation is "back" or "last") { last = value; continue; }
            bool matches = operation is "contains" or "find" ? NativeLanguageEquality.Instance.Equals(value, args[1])
                : Boolean(await invoke(args[1], [value], token));
            if (operation is "contains" && matches) return NativeValue.True;
            if (operation is "find" or "find_if" && matches) return value;
            if (operation is "any" or "any_of" && matches) return NativeValue.True;
            if (operation == "all_of" && !matches) return NativeValue.False;
            if (operation is "none" or "none_of" && matches) return NativeValue.False;
        }
        return operation switch
        {
            "empty" or "all_of" or "none" or "none_of" => NativeValue.True,
            "contains" or "any" or "any_of" => NativeValue.False,
            "back" or "last" => last ?? throw new MsyncException($"ranges.{operation} requires a nonempty Range"),
            "front" or "first" or "find" or "find_if" => throw new MsyncException($"ranges.{operation} found no matching item"),
            _ => throw new MsyncException($"Unknown range native '{operation}'")
        };
    }

    private static double FiniteNumber(NativeValue value, string operation, string module = "ranges")
    {
        double number = Number(value);
        return double.IsFinite(number) ? number : throw new MsyncException($"{module}.{operation} requires finite Number arguments");
    }
    private static long Ordinal(NativeValue value, string operation, bool positive, string module = "ranges") =>
        Ordinal(FiniteNumber(value, operation, module), operation, positive, module);
    private static long Ordinal(double number, string operation, bool positive, string module)
    {
        if (number < (positive ? 1 : 0) || number != Math.Truncate(number) || number >= 9223372036854775808d)
            throw new MsyncException($"{module}.{operation} requires a {(positive ? "positive" : "nonnegative")} integral count representable by the platform iterator");
        return (long)number;
    }

    internal static bool RangeEquals(NativeValue left, NativeValue right) => NativeLanguageEquality.Instance.Equals(left, right);

    internal sealed class NativeLanguageEquality : IEqualityComparer<NativeValue>
    {
        internal static NativeLanguageEquality Instance { get; } = new();
        public bool Equals(NativeValue left, NativeValue right)
        {
            if (left.Kind != right.Kind) return false;
            if (left.Kind == NativeKind.Number) return left.AsNumber().Equals(right.AsNumber());
            if (left.Kind == NativeKind.Range)
            {
                NativeRange a = left.AsRange(), b = right.AsRange();
                return ReferenceEquals(a, b) || ReferenceEquals(a.Items, b.Items) && a.ItemType == b.ItemType
                    && a.Selection == b.Selection && a.Resolve == b.Resolve;
            }
            return left.Equals(right);
        }
        public int GetHashCode(NativeValue value) => value.Kind switch
        {
            NativeKind.Number => value.AsNumber().GetHashCode(),
            NativeKind.Range => HashCode.Combine(value.AsRange().Items, value.AsRange().ItemType, value.AsRange().Selection, value.AsRange().Resolve),
            _ => value.GetHashCode()
        };
    }
}

public static class NativeRangeDemand
{
    public static async ValueTask<NativeRange> DemandAsync(NativeRange range, CancellationToken token = default)
    {
        while (range.Resolve is { } resolve) range = await resolve(token);
        return range;
    }

    public static NativeRange Defer(Func<CancellationToken, ValueTask<NativeRange>> evaluate,
        TypeRef itemType, IReadOnlyList<NativeValue>? roots = null, NativeType? physicalItemType = null)
    {
        Deferred source = new(evaluate);
        return new(source, itemType, roots, resolve: source.ResolveAsync, physicalItemType: physicalItemType);
    }

    private sealed class Deferred(Func<CancellationToken, ValueTask<NativeRange>> evaluate) : IAsyncEnumerable<NativeValue>
    {
        private readonly Lock gate = new();
        private Task<NativeRange>? pending;
        internal ValueTask<NativeRange> ResolveAsync(CancellationToken token)
        {
            lock (gate) return new(pending ??= EvaluateAsync(token));
        }
        private async Task<NativeRange> EvaluateAsync(CancellationToken token) => await evaluate(token);
        public async IAsyncEnumerator<NativeValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            NativeRange range = await DemandAsync(await ResolveAsync(cancellationToken), cancellationToken);
            await foreach (NativeValue value in range.Items.WithCancellation(cancellationToken)) yield return value;
        }
    }
}
