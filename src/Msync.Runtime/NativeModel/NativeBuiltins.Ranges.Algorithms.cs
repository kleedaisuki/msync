using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static bool IsExtendedRangeOperation(string name) => name is "concat" or "flatten" or "zip"
        or "enumerate_from" or "chunk" or "slide" or "materialize" or "sort" or "sort_with" or "scan"
        or "at" or "size" or "merge" or "merge_with" or "set_union" or "set_union_with"
        or "set_intersection" or "set_intersection_with" or "set_difference" or "set_difference_with"
        or "set_symmetric_difference" or "set_symmetric_difference_with";

    private static async ValueTask<NativeValue> ExtendedRangeOperationAsync(string operation, IReadOnlyList<NativeValue> args,
        NativeRuntimeContext context,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke, CancellationToken token)
    {
        NativeRange source = Range(args[0], operation);
        if (operation == "at") return await ElementAt(source, Ordinal(args[1], operation, false), token);
        if (operation == "materialize") return NativeValue.FromRange(await Materialized(source, context, token));
        if (operation == "size") return NativeValue.FromNumber(await CountRange(source, token));
        if (SortedCombination(operation) is { } combination)
        {
            NativeRange right = Range(args[1], operation);
            TypeRef element = combination is RangeCombination.Intersection or RangeCombination.Difference
                ? source.ItemType : TypeRef.Union(source.ItemType, right.ItemType);
            return NativeValue.FromRange(new(CombineSorted(source, right, combination,
                args.Count == 3 ? args[2] : (NativeValue?)null, invoke, token), element, args,
                physicalItemType: combination is RangeCombination.Intersection or RangeCombination.Difference
                    ? source.PhysicalItemType : NativeType.Union(source.PhysicalItemType, right.PhysicalItemType)));
        }
        NativeRange result = operation switch
        {
            "concat" => new(Concatenated(source, Range(args[1], operation), token), TypeRef.Union(source.ItemType, args[1].AsRange().ItemType), args,
                physicalItemType: NativeType.Union(source.PhysicalItemType, args[1].AsRange().PhysicalItemType)),
            "flatten" => new(Flattened(source, token), source.ItemType.Element ?? TypeRef.Any, args),
            "zip" => new(Zipped(source, Range(args[1], operation), context, token), TypeRef.Scope, args),
            "enumerate_from" => new(Indexed(source, FiniteNumber(args[1], operation), context, token), TypeRef.Scope, args),
            "chunk" => new(Partitioned(source, Ordinal(args[1], operation, true), false, context, token), new(TypeKind.Range, source.ItemType), args),
            "slide" => new(Partitioned(source, Ordinal(args[1], operation, true), true, context, token), new(TypeKind.Range, source.ItemType), args),
            "sort" or "sort_with" => Adapt(source, source.ItemType,
                (range, cancellation) => Sorted(range, operation == "sort_with" ? args[1] : (NativeValue?)null, invoke, cancellation), token, args.Skip(1).ToArray()),
            "scan" => new(Scanned(source, args[1], args[2], invoke, token), TypeRef.Any, args),
            _ => throw new MsyncException($"Unknown range native '{operation}'")
        };
        return NativeValue.FromRange(result);
    }

    private static async ValueTask<NativeValue> ElementAt(NativeRange source, long index, CancellationToken token)
    {
        source = await DemandRangeAsync(source, token);
        if (source.SnapshotValues is { } snapshot)
            return index < snapshot.Count ? snapshot[(int)index] : throw new MsyncException($"ranges.at has no item at index {index}");
        long position = 0;
        await foreach (NativeValue value in source.Items.WithCancellation(token))
            if (position++ == index) return value;
        throw new MsyncException($"ranges.at has no item at index {index}");
    }

    private static async ValueTask<long> CountRange(NativeRange source, CancellationToken token)
    {
        source = await DemandRangeAsync(source, token);
        token.ThrowIfCancellationRequested();
        if (source.SnapshotValues is { } snapshot) return snapshot.Count;
        long count = 0;
        await foreach (NativeValue unused in source.Items.WithCancellation(token)) count++;
        return count;
    }

    private static async ValueTask<NativeRange> Materialized(NativeRange source, NativeRuntimeContext context, CancellationToken token)
    {
        TypeRef type = source.ItemType;
        source = await DemandRangeAsync(source, token);
        List<NativeValue> values = [];
        await foreach (NativeValue value in source.Items.WithCancellation(token)) values.Add(value);
        return context.Snapshot(values, type, source.PhysicalItemType);
    }

    private static async IAsyncEnumerable<NativeValue> Concatenated(NativeRange first, NativeRange second,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeValue value in first.Items.WithCancellation(token)) yield return value;
        await foreach (NativeValue value in second.Items.WithCancellation(token)) yield return value;
    }

    private static async IAsyncEnumerable<NativeValue> Flattened(NativeRange source,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeValue range in source.Items.WithCancellation(token))
            await foreach (NativeValue value in Range(range, "flatten").Items.WithCancellation(token)) yield return value;
    }

    private static async IAsyncEnumerable<NativeValue> Zipped(NativeRange first, NativeRange second, NativeRuntimeContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        NativeLayout layout = NativeBuiltinLayouts.Zip(first.PhysicalItemType, second.PhysicalItemType);
        await using IAsyncEnumerator<NativeValue> left = first.Items.GetAsyncEnumerator(token);
        await using IAsyncEnumerator<NativeValue> right = second.Items.GetAsyncEnumerator(token);
        while (await left.MoveNextAsync() && await right.MoveNextAsync())
            yield return Record(context, layout, left.Current, right.Current);
    }

    private static async IAsyncEnumerable<NativeValue> Indexed(NativeRange source, double start, NativeRuntimeContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        NativeLayout layout = NativeBuiltinLayouts.Enumerate(source.PhysicalItemType);
        double index = start;
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            yield return Record(context, layout, NativeValue.FromNumber(index), value);
            double next = index + 1;
            if (next == index) throw new MsyncException("ranges.enumerate_from index no longer advances the Number value");
            index = next;
        }
    }

    private static async IAsyncEnumerable<NativeValue> Partitioned(NativeRange source, long count, bool sliding, NativeRuntimeContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        Queue<NativeValue> window = [];
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            window.Enqueue(value);
            if (window.Count != count) continue;
            yield return NativeValue.FromRange(context.Snapshot(window, source.ItemType, source.PhysicalItemType));
            if (sliding) window.Dequeue(); else window.Clear();
        }
        if (!sliding && window.Count > 0) yield return NativeValue.FromRange(context.Snapshot(window, source.ItemType, source.PhysicalItemType));
    }

    private static async IAsyncEnumerable<NativeValue> Scanned(NativeRange source, NativeValue initial, NativeValue operation,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        NativeValue accumulator = initial;
        await foreach (NativeValue value in source.Items.WithCancellation(token))
        {
            accumulator = await invoke(operation, [accumulator, value], token);
            yield return accumulator;
        }
    }

    private static async IAsyncEnumerable<NativeValue> Sorted(NativeRange source, NativeValue? less,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        List<NativeValue> values = [];
        await foreach (NativeValue value in source.Items.WithCancellation(token)) values.Add(value);
        await StableSort(values, less, invoke, token);
        foreach (NativeValue value in values) { token.ThrowIfCancellationRequested(); yield return value; }
    }

    internal static int CompareRangeValues(NativeValue left, NativeValue right)
    {
        if (left.Kind == NativeKind.Number && right.Kind == NativeKind.Number) return left.AsNumber().CompareTo(right.AsNumber());
        if (left.Kind == NativeKind.Bool && right.Kind == NativeKind.Bool) return left.AsBool().CompareTo(right.AsBool());
        if (left.Kind == NativeKind.String && right.Kind == NativeKind.String) return TextSemantics.CompareOrdinal(left.AsStringSpan(), right.AsStringSpan());
        if (left.Kind is NativeKind.String or NativeKind.Path && right.Kind is NativeKind.String or NativeKind.Path)
            return TextSemantics.CompareOrdinal(Text(left), Text(right));
        throw new MsyncException($"Natural range ordering cannot compare {left.Type} and {right.Type}; supply a strict comparator");
    }

    private static async ValueTask StableSort(List<NativeValue> values, NativeValue? less,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke, CancellationToken token)
    {
        NativeValue[] source = values.ToArray(), target = new NativeValue[source.Length];
        for (int width = 1; width < source.Length; width = width > source.Length / 2 ? source.Length : width * 2)
        {
            for (int start = 0; start < source.Length;)
            {
                int middle = start + Math.Min(width, source.Length - start);
                int end = middle + Math.Min(width, source.Length - middle);
                await Merge(source, target, start, middle, end, less, invoke, token);
                start = end;
            }
            (source, target) = (target, source);
        }
        for (int index = 0; index < source.Length; index++) values[index] = source[index];
    }

    private static async ValueTask Merge(NativeValue[] source, NativeValue[] target, int start, int middle, int end, NativeValue? less,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke, CancellationToken token)
    {
        int left = start, right = middle;
        for (int output = start; output < end; output++)
        {
            token.ThrowIfCancellationRequested();
            bool takeRight = right < end && (left == middle || (less is null
                ? CompareRangeValues(source[right], source[left]) < 0
                : Boolean(await invoke(less.Value, [source[right], source[left]], token))));
            target[output] = source[takeRight ? right++ : left++];
        }
    }

    private enum RangeCombination { Merge, Union, Intersection, Difference, SymmetricDifference }
    private static RangeCombination? SortedCombination(string name) => name switch
    {
        "merge" or "merge_with" => RangeCombination.Merge,
        "set_union" or "set_union_with" => RangeCombination.Union,
        "set_intersection" or "set_intersection_with" => RangeCombination.Intersection,
        "set_difference" or "set_difference_with" => RangeCombination.Difference,
        "set_symmetric_difference" or "set_symmetric_difference_with" => RangeCombination.SymmetricDifference,
        _ => null
    };

    private static async IAsyncEnumerable<NativeValue> CombineSorted(NativeRange first, NativeRange second,
        RangeCombination combination, NativeValue? less,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using IAsyncEnumerator<NativeValue> left = first.Items.GetAsyncEnumerator(token);
        bool takeLeft = combination != RangeCombination.Intersection;
        bool takeRight = combination is RangeCombination.Merge or RangeCombination.Union or RangeCombination.SymmetricDifference;
        bool takeEqual = combination is RangeCombination.Union or RangeCombination.Intersection;
        bool hasLeft = await left.MoveNextAsync();
        if (!hasLeft && !takeRight) yield break;
        await using IAsyncEnumerator<NativeValue> right = second.Items.GetAsyncEnumerator(token);
        bool hasRight = await right.MoveNextAsync();
        while (hasLeft && hasRight)
        {
            token.ThrowIfCancellationRequested();
            NativeValue x = left.Current, y = right.Current;
            if (await Less(y, x))
            {
                if (takeRight) yield return y;
                hasRight = await right.MoveNextAsync();
                continue;
            }
            if (combination == RangeCombination.Merge || await Less(x, y))
            {
                if (takeLeft) yield return x;
                hasLeft = await left.MoveNextAsync();
                continue;
            }
            if (takeEqual) yield return x;
            hasLeft = await left.MoveNextAsync();
            if (!hasLeft && !takeRight) yield break;
            hasRight = await right.MoveNextAsync();
        }
        if (takeLeft)
            while (hasLeft) { token.ThrowIfCancellationRequested(); yield return left.Current; hasLeft = await left.MoveNextAsync(); }
        if (takeRight)
            while (hasRight) { token.ThrowIfCancellationRequested(); yield return right.Current; hasRight = await right.MoveNextAsync(); }
        async ValueTask<bool> Less(NativeValue a, NativeValue b) => less is null
            ? CompareRangeValues(a, b) < 0 : Boolean(await invoke(less.Value, [a, b], token));
    }
}
