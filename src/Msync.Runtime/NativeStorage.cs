using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static async ValueTask<Value> StorageOperationAsync(string operation, IReadOnlyList<Value> args,
        CancellationToken token)
    {
        RangeValue range = (RangeValue)args[0];
        if (operation == "buffer_new")
        {
            List<Value> values = [];
            await foreach (Value value in range.Items.WithCancellation(token)) values.Add(value);
            return new RangeValue(new IndexedItems(values, range.ItemType), range.ItemType) { RetainedValues = values };
        }
        if (range.Items is not IndexedItems buffer)
            throw new MsyncException($"storage.{operation} requires Buffer storage; construct it with Buffer::new.");
        List<Value> items = buffer.Values;
        switch (operation)
        {
            case "buffer_size": return new NumberValue(items.Count);
            case "buffer_snapshot": return new RangeValue(items.ToArray(), range.ItemType);
            case "buffer_clear": items.Clear(); return Value.Null;
            case "buffer_get": return items[BufferIndex(args[1], items.Count, false, operation)];
            case "buffer_set":
                items[BufferIndex(args[1], items.Count, false, operation)] = args[2];
                range.InferItemType(args[2]);
                return Value.Null;
            case "buffer_push":
                int next = items.Count;
                items.Add(args[1]);
                range.InferItemType(args[1]);
                return new NumberValue(next);
            case "buffer_pop":
                if (items.Count == 0) throw new MsyncException("Buffer::pop requires a nonempty buffer.");
                int last = items.Count - 1;
                Value tail = items[last];
                items.RemoveAt(last);
                return tail;
            case "buffer_insert":
                items.Insert(BufferIndex(args[1], items.Count, true, operation), args[2]);
                range.InferItemType(args[2]);
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

    private static int BufferIndex(Value value, int count, bool insert, string operation)
    {
        double index = ((NumberValue)value).Data;
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
