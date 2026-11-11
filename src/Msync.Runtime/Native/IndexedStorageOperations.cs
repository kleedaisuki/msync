using Msync.Core;

namespace Msync.Runtime;

internal static class IndexedStorageOperations
{
    internal static async ValueTask<Value> InvokeAsync(string operation, IReadOnlyList<Value> args,
        RuntimeContext context, CancellationToken token)
    {
        RangeValue range = (RangeValue)args[0];
        if (operation == "buffer_new")
        {
            List<Value> values = [];
            await foreach (Value value in range.Items.WithCancellation(token)) values.Add(value);
            return new RangeValue(new IndexedItems(values, range.ItemType), range.ItemType) { RetainedValues = values };
        }
        if (!TryGetItems(range, out List<Value> items))
            throw new MsyncException($"storage.{operation} requires Buffer storage; construct it with Buffer::new.");
        switch (operation)
        {
            case "buffer_size": return new NumberValue(items.Count);
            case "buffer_snapshot": return new RangeValue(items.ToArray(), range.ItemType);
            case "buffer_clear": items.Clear(); return Value.Null;
            case "buffer_get": return Get(items, ((NumberValue)args[1]).Data);
            case "buffer_set":
                Value replacement = StoreValue(range, args[2], context);
                return SetConverted(range, items, ((NumberValue)args[1]).Data, replacement);
            case "buffer_push":
                int next = items.Count;
                Value appended = StoreValue(range, args[1], context);
                items.Add(appended);
                range.InferItemType(appended);
                return new NumberValue(next);
            case "buffer_pop":
                if (items.Count == 0) throw new MsyncException("Buffer::pop requires a nonempty buffer.");
                int last = items.Count - 1;
                Value tail = items[last];
                items.RemoveAt(last);
                return tail;
            case "buffer_insert":
                Value inserted = StoreValue(range, args[2], context);
                items.Insert(BufferIndex(args[1], items.Count, true, operation), inserted);
                range.InferItemType(inserted);
                return Value.Null;
            case "buffer_remove":
                int index = BufferIndex(args[1], items.Count, false, operation);
                Value removed = items[index];
                items.RemoveAt(index);
                return removed;
            case "buffer_swap":
                int first = BufferIndex(args[1], items.Count, false, operation);
                int second = BufferIndex(args[2], items.Count, false, operation);
                (items[first], items[second]) = (items[second], items[first]);
                return Value.Null;
            default: throw new MsyncException($"Unknown storage native '{operation}'.");
        }
    }

    internal static bool TryGetItems(RangeValue range, out List<Value> items)
    {
        if (range.Items is IndexedItems buffer)
        {
            items = buffer.Values;
            return true;
        }
        items = null!;
        return false;
    }

    internal static Value Get(List<Value> items, double index) =>
        items[BufferIndex(index, items.Count, false, "buffer_get")];

    internal static Value Set(RangeValue range, List<Value> items, double index, Value replacement,
        RuntimeContext context) => SetConverted(range, items, index, StoreValue(range, replacement, context));

    private static Value SetConverted(RangeValue range, List<Value> items, double index, Value replacement)
    {
        items[BufferIndex(index, items.Count, false, "buffer_set")] = replacement;
        range.InferItemType(replacement);
        return Value.Null;
    }

    // A selected resource can have a different text representation from another
    // resource in the same static view; conversion belongs to the actual target.
    private static Value StoreValue(RangeValue range, Value value, RuntimeContext context) => value switch
    {
        StringValue when range.ItemType.WithoutNull == TypeRef.Path => context.AsPath(value),
        PathValue path when range.ItemType.WithoutNull == TypeRef.String => new StringValue(path.NativeText),
        _ => value
    };

    private static int BufferIndex(Value value, int count, bool insert, string operation) =>
        BufferIndex(((NumberValue)value).Data, count, insert, operation);

    private static int BufferIndex(double index, int count, bool insert, string operation)
    {
        if (index >= 0 && index <= (insert ? count : count - 1) && index == Math.Truncate(index))
            return (int)index;
        throw new MsyncException($"storage.{operation} index {index} is outside the {(insert ? "insertion positions" : "items")} of a buffer with {count} items.");
    }

    // The buffer owns its backing list. Ordinary Range materialization and the
    // snapshot operation publish separate membership arrays, not live aliases.
    private sealed class IndexedItems(List<Value> values, TypeRef initialItemType) : IAsyncEnumerable<Value>, IRangeTypeOrigin
    {
        public List<Value> Values { get; } = values;
        public TypeRef InitialItemType { get; } = initialItemType;
        public async IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            for (int index = 0; index < Values.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Values[index];
            }
            await Task.CompletedTask;
        }
    }
}
