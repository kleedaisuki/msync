using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private static async IAsyncEnumerable<NativeDatum> RepeatDatum(NativeDatum value, long count, [EnumeratorCancellation] CancellationToken token)
    {
        for (long index = 0; index < count; index++) { token.ThrowIfCancellationRequested(); yield return value; }
        await Task.CompletedTask;
    }

    private async IAsyncEnumerable<NativeDatum> CountedDatums(NativeRange source, string operation, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (operation == "take" && count == 0) yield break;
        long remaining = count;
        await foreach (NativeDatum value in DatumItems(source, token))
        {
            if (operation == "take") { yield return value; if (--remaining == 0) yield break; continue; }
            if (operation == "drop") { if (remaining == 0) yield return value; else remaining--; continue; }
            if (remaining == count) yield return value;
            if (--remaining == 0) remaining = count;
        }
    }

    private async IAsyncEnumerable<NativeDatum> CallbackDatums(NativeRange source, string operation, NativeValue callback,
        [EnumeratorCancellation] CancellationToken token)
    {
        bool dropping = operation == "drop_while";
        await using IAsyncEnumerator<NativeDatum> iterator = GetDatumIterator(source, token);
        while (await iterator.MoveNextAsync())
        {
            NativeDatum value = iterator.Current;
            if (operation == "drop_while" && !dropping) { yield return value; continue; }
            NativeDatum result = await InvokeDatumAsync(callback, [value], token);
            if (operation == "transform") { yield return result; continue; }
            bool selected = (await result.ResolveAsync(token)).AsBool();
            if (operation == "take_while" && !selected) yield break;
            if (operation == "drop_while") { dropping = selected; if (!selected) yield return value; continue; }
            if (selected) yield return value;
        }
    }

    private async IAsyncEnumerable<NativeDatum> ReversedDatums(NativeRange source, [EnumeratorCancellation] CancellationToken token)
    {
        List<NativeDatum> values = await CollectDatums(source, token);
        for (int index = values.Count - 1; index >= 0; index--) { token.ThrowIfCancellationRequested(); yield return values[index]; }
    }

    private async IAsyncEnumerable<NativeDatum> ConcatDatums(NativeRange first, NativeRange second, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeDatum value in DatumItems(first, token)) yield return value;
        await foreach (NativeDatum value in DatumItems(second, token)) yield return value;
    }

    private async IAsyncEnumerable<NativeDatum> FlattenDatums(NativeRange source, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeDatum value in DatumItems(source, token))
            await foreach (NativeDatum item in DatumItems((await value.ResolveAsync(token)).AsRange(), token)) yield return item;
    }

    private NativeDatum DatumRecord(NativeLayout layout, params NativeDatum[] values)
    {
        NativeAllocation allocation = guestHeap.NewObject(layout, -1, 1, 0, currentWork.Value?.Counters ?? default);
        for (int index = 0; index < values.Length; index++) memory.Write(allocation.Field(index), values[index]);
        return new(NativeValue.FromAddress(allocation.Address));
    }

    private async IAsyncEnumerable<NativeDatum> ZipDatums(NativeRange first, NativeRange second, [EnumeratorCancellation] CancellationToken token)
    {
        NativeLayout layout = NativeBuiltinLayouts.Zip(first.PhysicalItemType, second.PhysicalItemType);
        await using IAsyncEnumerator<NativeDatum> left = GetDatumIterator(first, token);
        await using IAsyncEnumerator<NativeDatum> right = GetDatumIterator(second, token);
        while (await left.MoveNextAsync() && await right.MoveNextAsync()) yield return DatumRecord(layout, left.Current, right.Current);
    }

    private async IAsyncEnumerable<NativeDatum> IndexedDatums(NativeRange source, double index, [EnumeratorCancellation] CancellationToken token)
    {
        NativeLayout layout = NativeBuiltinLayouts.Enumerate(source.PhysicalItemType);
        await using IAsyncEnumerator<NativeDatum> iterator = GetDatumIterator(source, token);
        while (await iterator.MoveNextAsync())
        {
            yield return DatumRecord(layout, new(NativeValue.FromNumber(index)), iterator.Current);
            double next = index + 1;
            if (next == index) throw new MsyncException("ranges.enumerate_from index no longer advances the Number value");
            index = next;
        }
    }

    private async IAsyncEnumerable<NativeDatum> PartitionDatums(NativeRange source, long count, bool sliding,
        [EnumeratorCancellation] CancellationToken token)
    {
        Queue<NativeDatum> window = [];
        await foreach (NativeDatum value in DatumItems(source, token))
        {
            window.Enqueue(value);
            if (window.Count != count) continue;
            yield return new(NativeValue.FromRange(DatumSnapshot(window.ToArray(), source.ItemType, source.PhysicalItemType)));
            if (sliding) window.Dequeue(); else window.Clear();
        }
        if (!sliding && window.Count > 0) yield return new(NativeValue.FromRange(DatumSnapshot(window.ToArray(), source.ItemType, source.PhysicalItemType)));
    }

    private async IAsyncEnumerable<NativeDatum> ScanDatums(NativeRange source, NativeDatum accumulator, NativeValue callback,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (NativeDatum value in DatumItems(source, token))
        {
            accumulator = await InvokeDatumAsync(callback, [accumulator, value], token);
            yield return accumulator;
        }
    }

    private async ValueTask<bool> LessDatum(NativeDatum left, NativeDatum right, NativeValue? callback, CancellationToken token) =>
        callback is { } function ? (await (await InvokeDatumAsync(function, [left, right], token)).ResolveAsync(token)).AsBool()
            : NativeBuiltins.CompareRangeValues(await left.ResolveAsync(token), await right.ResolveAsync(token)) < 0;

    private async IAsyncEnumerable<NativeDatum> SortDatums(NativeRange range, NativeValue? less, [EnumeratorCancellation] CancellationToken token)
    {
        NativeDatum[] source = (await CollectDatums(range, token)).ToArray(), target = new NativeDatum[source.Length];
        for (int width = 1; width < source.Length; width = width > source.Length / 2 ? source.Length : width * 2)
        {
            for (int start = 0; start < source.Length;)
            {
                int middle = start + Math.Min(width, source.Length - start), end = middle + Math.Min(width, source.Length - middle);
                int left = start, right = middle;
                for (int output = start; output < end; output++)
                {
                    token.ThrowIfCancellationRequested();
                    bool takeRight = right < end && (left == middle || await LessDatum(source[right], source[left], less, token));
                    target[output] = source[takeRight ? right++ : left++];
                }
                start = end;
            }
            (source, target) = (target, source);
        }
        foreach (NativeDatum value in source) { token.ThrowIfCancellationRequested(); yield return value; }
    }

    private async IAsyncEnumerable<NativeDatum> UniqueDatums(NativeRange range, [EnumeratorCancellation] CancellationToken token)
    {
        HashSet<NativeValue> ready = new(NativeBuiltins.NativeLanguageEquality.Instance);
        HashSet<NativeFuture> pending = new(ReferenceEqualityComparer.Instance);
        await foreach (NativeDatum value in DatumItems(range, token))
            if (value.Future is { } future ? pending.Add(future.Identity) : ready.Add(value.Value)) yield return value;
    }

    private async IAsyncEnumerable<NativeDatum> CombineDatums(NativeRange first, NativeRange second, string operation, NativeValue? less,
        [EnumeratorCancellation] CancellationToken token)
    {
        bool intersection = operation.StartsWith("set_intersection", StringComparison.Ordinal);
        bool difference = operation.StartsWith("set_difference", StringComparison.Ordinal);
        bool merge = operation is "merge" or "merge_with";
        bool union = operation is "set_union" or "set_union_with";
        bool takeLeft = !intersection, takeRight = !intersection && !difference, takeEqual = union || intersection;
        await using IAsyncEnumerator<NativeDatum> left = GetDatumIterator(first, token);
        bool hasLeft = await left.MoveNextAsync();
        if (!hasLeft && !takeRight) yield break;
        await using IAsyncEnumerator<NativeDatum> right = GetDatumIterator(second, token);
        bool hasRight = await right.MoveNextAsync();
        while (hasLeft && hasRight)
        {
            NativeDatum x = left.Current, y = right.Current;
            if (await LessDatum(y, x, less, token))
            {
                if (takeRight) yield return y;
                hasRight = await right.MoveNextAsync();
                continue;
            }
            if (merge || await LessDatum(x, y, less, token))
            {
                if (takeLeft) yield return x;
                hasLeft = await left.MoveNextAsync();
                continue;
            }
            if (takeEqual) yield return x;
            hasLeft = await left.MoveNextAsync();
            hasRight = await right.MoveNextAsync();
        }
        while (hasLeft && takeLeft) { yield return left.Current; hasLeft = await left.MoveNextAsync(); }
        while (hasRight && takeRight) { yield return right.Current; hasRight = await right.MoveNextAsync(); }
    }
}
