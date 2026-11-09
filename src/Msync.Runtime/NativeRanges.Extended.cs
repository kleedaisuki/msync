using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static readonly ScopeLayout PairLayout = new(["first", "second"]);
    private static readonly ScopeLayout IndexedLayout = new(["index", "value"]);

    private static bool IsExtendedRangeOperation(string name) => name is "concat" or "flatten" or "zip"
        or "enumerate_from" or "chunk" or "slide" or "materialize" or "sort" or "sort_with" or "scan" or "sequence" or "repeat" or "at";

    private static ValueTask<Value> ExtendedRangeOperationAsync(string name, IReadOnlyList<Value> args,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token)
    {
        if (name == "repeat")
            return new(new RangeValue(Repeated(args[0], Ordinal(args[1], name, false), token), args[0].Type));
        if (name == "sequence")
        {
            double start = FiniteNumber(args[0], name), stop = FiniteNumber(args[1], name), step = FiniteNumber(args[2], name);
            if (step == 0) throw new MsyncException("ranges.sequence requires a nonzero step");
            return new(new RangeValue(NumberSequence(start, stop, step, token), TypeRef.Number));
        }
        RangeValue source = Range(args[0], name);
        if (name == "at") return ElementAt(source, Ordinal(args[1], name, false), token);
        if (name == "materialize") return Materialized(source, token);
        Value result = name switch
        {
            "concat" => Concatenated(source, Range(args[1], name), token),
            "flatten" => new RangeValue(Flattened(source, token), source.ItemType.Element ?? TypeRef.Any),
            "zip" => new RangeValue(Zipped(source, Range(args[1], name), token), TypeRef.Scope),
            "enumerate_from" => new RangeValue(Indexed(source, FiniteNumber(args[1], name), token), TypeRef.Scope),
            "chunk" => new RangeValue(Partitioned(source, Ordinal(args[1], name, true), false, token), new(TypeKind.Range, source.ItemType)),
            "slide" => new RangeValue(Partitioned(source, Ordinal(args[1], name, true), true, token), new(TypeKind.Range, source.ItemType)),
            "sort" => Adapt(source, source.ItemType, (values, cancellation) => Sorted(values, null, invoke, cancellation), token),
            "sort_with" => Adapt(source, source.ItemType, (values, cancellation) => Sorted(values, args[1], invoke, cancellation), token),
            "scan" => new RangeValue(Scanned(source, args[1], args[2], invoke, token), TypeRef.Any),
            _ => throw new MsyncException($"Unknown range native '{name}'")
        };
        return new(result);
    }

    private static RangeValue Range(Value value, string operation) => value as RangeValue
        ?? throw new MsyncException($"ranges.{operation} expects Range, received {value.Type}");

    private static double FiniteNumber(Value value, string operation) => value is NumberValue number && double.IsFinite(number.Data)
        ? number.Data : throw new MsyncException($"ranges.{operation} requires finite Number arguments");

    private static long Ordinal(Value value, string operation, bool positive)
    {
        double number = FiniteNumber(value, operation);
        if (number < (positive ? 1 : 0) || number != Math.Truncate(number) || number >= 9223372036854775808d)
            throw new MsyncException($"ranges.{operation} requires a {(positive ? "positive" : "nonnegative")} integral count representable by the platform iterator");
        return (long)number;
    }

    private static async IAsyncEnumerable<Value> Repeated(Value value, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        for (long index = 0; index < count; index++) { token.ThrowIfCancellationRequested(); yield return value; }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Value> NumberSequence(double start, double stop, double step,
        [EnumeratorCancellation] CancellationToken token)
    {
        for (double value = start; step > 0 ? value < stop : value > stop;)
        {
            token.ThrowIfCancellationRequested();
            yield return new NumberValue(value);
            double next = value + step;
            if (next == value) throw new MsyncException("ranges.sequence step no longer advances the Number value");
            value = next;
        }
        await Task.CompletedTask;
    }

    private static RangeValue Concatenated(RangeValue first, RangeValue second, CancellationToken token) =>
        new(ConcatenatedItems(first, second, token), TypeRef.Union(first.ItemType, second.ItemType));

    private static async ValueTask<Value> ElementAt(RangeValue source, long index, CancellationToken token)
    {
        long position = 0;
        await foreach (Value value in source.Items.WithCancellation(token))
            if (position++ == index) return value;
        throw new MsyncException($"ranges.at has no item at index {index}");
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
        List<Value> values = [];
        await foreach (Value value in source.Items.WithCancellation(token)) values.Add(value);
        return new RangeValue(values, source.ItemType);
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
        _ => throw new MsyncException($"ranges.sort cannot order {left.Type} and {right.Type}; use sort_with for a custom order")
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
