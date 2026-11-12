using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static readonly ScopeLayout PairLayout = new(["first", "second"]);
    private static readonly ScopeLayout IndexedLayout = new(["index", "value"]);

    private static bool IsExtendedRangeOperation(string name) => name is "concat" or "flatten" or "zip"
        or "enumerate_from" or "chunk" or "slide" or "materialize" or "sort" or "sort_with" or "scan" or "sequence"
        or "repeat" or "at" or "empty_of" or "less" or "size"
        or "merge" or "merge_with" or "set_union" or "set_union_with" or "set_intersection" or "set_intersection_with"
        or "set_difference" or "set_difference_with" or "set_symmetric_difference" or "set_symmetric_difference_with";

    private static ValueTask<Value> ExtendedRangeOperationAsync(string name, IReadOnlyList<Value> args,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token)
    {
        if (name == "empty_of") return new(new RangeValue(Array.Empty<Value>(), ((TypeValue)args[0]).Data));
        if (name == "less") return new(Bool(CompareRangeValues(args[0], args[1]) < 0));
        if (name == "repeat")
            return new(new RangeValue(Repeated(args[0], Ordinal(args[1], name, false), token), args[0].Type));
        if (name == "sequence")
        {
            double start = FiniteNumber(args[0], name), stop = FiniteNumber(args[1], name), step = FiniteNumber(args[2], name);
            if (step == 0) throw new MsyncException("ranges.sequence requires a nonzero step");
            return new(RangeValue.NumberSequence(start, stop, step, token));
        }
        RangeValue source = Range(args[0], name);
        if (name == "at") return ElementAt(source, Ordinal(args[1], name, false), token);
        if (name == "materialize") return Materialized(source, token);
        if (name == "size") return CountRange(source, token);
        if (SortedCombination(name) is { } combination)
        {
            var right = Range(args[1], name);
            var element = combination is RangeCombination.Intersection or RangeCombination.Difference
                ? source.ItemType : TypeRef.Union(source.ItemType, right.ItemType);
            return new(new RangeValue(CombineSorted(source, right, combination, args.Count == 3 ? args[2] : null, invoke, token), element));
        }
        Value result = name switch
        {
            "concat" => Concatenated(source, Range(args[1], name), token),
            "flatten" => new RangeValue(Flattened(source, token), source.ItemType.Element ?? TypeRef.Any),
            "zip" => new RangeValue(Zipped(source, Range(args[1], name), token), TypeRef.Scope),
            "enumerate_from" => new RangeValue(Indexed(source, FiniteNumber(args[1], name), token), TypeRef.Scope),
            "chunk" => new RangeValue(Partitioned(source, Ordinal(args[1], name, true), false, token), new(TypeKind.Range, source.ItemType)),
            "slide" => new RangeValue(Partitioned(source, Ordinal(args[1], name, true), true, token), new(TypeKind.Range, source.ItemType)),
            "sort" or "sort_with" => AdaptSort(source, name == "sort_with" ? args : null, invoke, token),
            "scan" => new RangeValue(Scanned(source, args[1], args[2], invoke, token), TypeRef.Any),
            _ => throw new MsyncException($"Unknown range native '{name}'")
        };
        return new(result);
    }

    private static RangeValue AdaptSort(RangeValue source, IReadOnlyList<Value>? args,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token) =>
        Adapt(source, source.ItemType, (values, cancellation) => Sorted(values, args?[1], invoke, cancellation), token);

    private static RangeValue Range(Value value, string operation) => value as RangeValue
        ?? throw new MsyncException($"ranges.{operation} expects Range, received {value.Type}");

    private static double FiniteNumber(Value value, string operation, string module = "ranges") => value is NumberValue number && double.IsFinite(number.Data)
        ? number.Data : throw new MsyncException($"{module}.{operation} requires finite Number arguments");

    private static long Ordinal(Value value, string operation, bool positive, string module = "ranges")
        => Ordinal(FiniteNumber(value, operation, module), operation, positive, module);

    private static long Ordinal(double number, string operation, bool positive, string module)
    {
        if (number < (positive ? 1 : 0) || number != Math.Truncate(number) || number >= 9223372036854775808d)
            throw new MsyncException($"{module}.{operation} requires a {(positive ? "positive" : "nonnegative")} integral count representable by the platform iterator");
        return (long)number;
    }

    private static async IAsyncEnumerable<Value> Repeated(Value value, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        for (long index = 0; index < count; index++) { token.ThrowIfCancellationRequested(); yield return value; }
        await Task.CompletedTask;
    }

    private static RangeValue Concatenated(RangeValue first, RangeValue second, CancellationToken token) =>
        new(ConcatenatedItems(first, second, token), TypeRef.Union(first.ItemType, second.ItemType));

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

    private static async IAsyncEnumerable<Value> CombineSorted(RangeValue first, RangeValue second,
        RangeCombination combination, Value? less,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using var left = first.Items.GetAsyncEnumerator(token);
        bool takeLeft = combination != RangeCombination.Intersection;
        bool takeRight = combination is RangeCombination.Merge or RangeCombination.Union or RangeCombination.SymmetricDifference;
        bool takeEqual = combination is RangeCombination.Union or RangeCombination.Intersection;
        bool hasLeft = await left.MoveNextAsync();
        if (!hasLeft && !takeRight) yield break;
        await using var right = second.Items.GetAsyncEnumerator(token);
        bool hasRight = await right.MoveNextAsync();
        while (hasLeft && hasRight)
        {
            token.ThrowIfCancellationRequested();
            var x = left.Current;
            var y = right.Current;
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
            while (hasLeft)
            {
                token.ThrowIfCancellationRequested();
                yield return left.Current;
                hasLeft = await left.MoveNextAsync();
            }
        if (takeRight)
            while (hasRight)
            {
                token.ThrowIfCancellationRequested();
                yield return right.Current;
                hasRight = await right.MoveNextAsync();
            }

        async ValueTask<bool> Less(Value a, Value b) => less is null
            ? CompareRangeValues(a, b) < 0 : Boolean(await invoke(less, [a, b], token));
    }

    private static async ValueTask<Value> ElementAt(RangeValue source, long index, CancellationToken token)
    {
        if (source.SnapshotValues is { } snapshot)
        {
            token.ThrowIfCancellationRequested();
            return SnapshotItem(snapshot, index);
        }
        source = await DeferredRangeSource.Demand(source, token);
        if (source.OwnedSnapshot is { } owned)
        {
            token.ThrowIfCancellationRequested();
            return SnapshotItem(owned, index);
        }
        if (source.Items is NumberRangeItems numbers)
        {
            var cursor = numbers.Open(token);
            long remaining = index;
            while (cursor.TryNext(out double number))
            {
                token.ThrowIfCancellationRequested();
                if (remaining-- == 0) return new NumberValue(number);
            }
            throw MissingIndex(index);
        }
        if (source.Items is StringRangeItems.Sync strings)
        {
            long remaining = index;
            foreach (var slice in strings.Read())
            {
                token.ThrowIfCancellationRequested();
                if (remaining-- == 0) return new StringValue(slice.ToOwnedString());
            }
            throw MissingIndex(index);
        }
        if (source.Items is StringRangeItems.Async stream)
        {
            long remaining = index;
            await foreach (var slice in stream.Read.WithCancellation(token))
                if (remaining-- == 0) return new StringValue(slice.ToOwnedString());
            throw MissingIndex(index);
        }
        long position = 0;
        await foreach (Value value in source.Items.WithCancellation(token))
            if (position++ == index) return value;
        throw MissingIndex(index);
    }

    internal static Value SnapshotAt(IReadOnlyList<Value> snapshot, double index)
    {
        if (!double.IsFinite(index)) throw new MsyncException("ranges.at requires finite Number arguments");
        return SnapshotItem(snapshot, Ordinal(index, "at", false, "ranges"));
    }

    private static Value SnapshotItem(IReadOnlyList<Value> snapshot, long index) =>
        index < snapshot.Count ? snapshot[(int)index] : throw MissingIndex(index);

    private static MsyncException MissingIndex(long index) => new($"ranges.at has no item at index {index}");

    private static async ValueTask<Value> CountRange(RangeValue range, CancellationToken token)
    {
        if (range.SnapshotValues is { } snapshot) return new NumberValue(snapshot.Count);
        range = await DeferredRangeSource.Demand(range, token);
        if (range.OwnedSnapshot is { } owned) return new NumberValue(owned.Count);
        long count = 0;
        if (range.Items is NumberRangeItems numbers)
        {
            var cursor = numbers.Open(token);
            while (cursor.TryNext(out _)) { token.ThrowIfCancellationRequested(); count++; }
        }
        else if (range.Items is StringRangeItems.Sync strings)
            foreach (var _ in strings.Read()) { token.ThrowIfCancellationRequested(); count++; }
        else if (range.Items is StringRangeItems.Async stream)
            await foreach (var _ in stream.Read.WithCancellation(token)) count++;
        else await foreach (var _ in range.Items.WithCancellation(token)) count++;
        return new NumberValue(count);
    }

    private static async IAsyncEnumerable<Value> ConcatenatedItems(RangeValue first, RangeValue second,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (Value value in first.Items.WithCancellation(token)) yield return value;
        await foreach (Value value in second.Items.WithCancellation(token)) yield return value;
    }

    private static async IAsyncEnumerable<Value> Flattened(RangeValue source,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (Value item in source.Items.WithCancellation(token))
            await foreach (Value value in Range(item, "flatten").Items.WithCancellation(token)) yield return value;
    }

    private static async IAsyncEnumerable<Value> Zipped(RangeValue first, RangeValue second,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using IAsyncEnumerator<Value> left = first.Items.GetAsyncEnumerator(token);
        await using IAsyncEnumerator<Value> right = second.Items.GetAsyncEnumerator(token);
        while (await left.MoveNextAsync() && await right.MoveNextAsync())
            yield return new ScopeValue(PairLayout, [left.Current, right.Current]);
    }

    private static async IAsyncEnumerable<Value> Indexed(RangeValue source, double start,
        [EnumeratorCancellation] CancellationToken token)
    {
        double index = start;
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            yield return new ScopeValue(IndexedLayout, [new NumberValue(index), value]);
            double next = index + 1;
            if (next == index) throw new MsyncException("ranges.enumerate_from index no longer advances the Number value");
            index = next;
        }
    }

    private static async IAsyncEnumerable<Value> Partitioned(RangeValue source, long count, bool sliding,
        [EnumeratorCancellation] CancellationToken token)
    {
        Queue<Value> window = new();
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            window.Enqueue(value);
            if (window.Count != count) continue;
            yield return new RangeValue(window.ToArray(), source.ItemType);
            if (sliding) window.Dequeue();
            else window.Clear();
        }
        if (!sliding && window.Count > 0) yield return new RangeValue(window.ToArray(), source.ItemType);
    }

    private static async ValueTask<Value> Materialized(RangeValue source, CancellationToken token)
    {
        TypeRef itemType = source.ItemType;
        source = await DeferredRangeSource.Demand(source, token);
        if (source.Items is NumberRangeItems numbers)
        {
            if (numbers is NumberRangeItems.Snapshot snapshot)
            {
                if (snapshot.Count != 0) token.ThrowIfCancellationRequested();
                return RangeValue.CopyNumberSnapshot(snapshot, itemType);
            }
            List<double> vector = [];
            var cursor = numbers.Open(token);
            while (cursor.TryNext(out double number))
            {
                token.ThrowIfCancellationRequested();
                vector.Add(number);
            }
            return RangeValue.OwnNumberSnapshot(vector, itemType);
        }
        if (source.Items is StringRangeItems text)
        {
            if (text is StringRangeItems.Snapshot snapshot)
            {
                if (snapshot.Count != 0) token.ThrowIfCancellationRequested();
                return RangeValue.CopyTextSnapshot(snapshot, itemType);
            }
            List<string> vector = [];
            if (text is StringRangeItems.Sync strings)
            {
                foreach (var slice in strings.Read())
                {
                    token.ThrowIfCancellationRequested();
                    vector.Add(slice.ToOwnedString());
                }
            }
            else
                await foreach (var slice in ((StringRangeItems.Async)text).Read.WithCancellation(token))
                    vector.Add(slice.ToOwnedString());
            return RangeValue.OwnTextSnapshot(vector, itemType);
        }

        List<Value> values = [];
        await foreach (Value value in source.Items.WithCancellation(token)) values.Add(value);
        return RangeValue.TakeSnapshotOwnership(values, itemType);
    }

    private static async IAsyncEnumerable<Value> Scanned(RangeValue source, Value initial, Value operation,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        Value accumulator = initial;
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            accumulator = await invoke(operation, [accumulator, value], token);
            yield return accumulator;
        }
    }

    private static async IAsyncEnumerable<Value> Sorted(RangeValue source, Value? less,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        List<Value> values = [];
        await foreach (Value value in source.Items.WithCancellation(token)) values.Add(value);
        await StableSort(values, less, invoke, token);
        foreach (Value value in values) { token.ThrowIfCancellationRequested(); yield return value; }
    }

    private static int CompareRangeValues(Value left, Value right) => (left, right) switch
    {
        (NumberValue first, NumberValue second) => first.Data.CompareTo(second.Data),
        (BoolValue first, BoolValue second) => first.Data.CompareTo(second.Data),
        (StringValue or PathValue, StringValue or PathValue) => TextSemantics.CompareOrdinal(Text(left), Text(right)),
        _ => throw new MsyncException($"Natural range ordering cannot compare {left.Type} and {right.Type}; supply a strict comparator")
    };

    private static async ValueTask StableSort(List<Value> values, Value? less,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token)
    {
        Value[] source = values.ToArray(), target = new Value[source.Length];
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

    private static async ValueTask Merge(Value[] source, Value[] target, int start, int middle, int end, Value? less,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token)
    {
        int left = start, right = middle;
        for (int output = start; output < end; output++)
        {
            token.ThrowIfCancellationRequested();
            bool takeRight = right < end && (left == middle || (less is null
                ? CompareRangeValues(source[right], source[left]) < 0
                : Boolean(await invoke(less, [source[right], source[left]], token))));
            target[output] = source[takeRight ? right++ : left++];
        }
    }
}
