using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static async ValueTask<Value> RangeOperationAsync(string name, IReadOnlyList<Value> args,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token)
    {
        if (IsExtendedRangeOperation(name))
            return await ExtendedRangeOperationAsync(name, args, invoke, token);
        if (name == "of") return new RangeValue(args, args.Aggregate(new TypeRef(TypeKind.Never), (type, value) => TypeRef.Union(type, value.Type)));
        if (name == "single") return new RangeValue([args[0]], args[0].Type);
        RangeValue source = args[0] as RangeValue ?? throw new MsyncException($"ranges.{name} expects Range");
        if (name is "identity" or "all") return source;
        if (name is "filter" or "transform" or "take_while" or "drop_while")
            return Adapt(source, name == "transform" ? TypeRef.Any : source.ItemType,
                (range, cancellation) => CallbackRange(range, name, args[1], invoke, cancellation), token);
        if (name is "take" or "drop" or "stride" or "step")
        {
            long count = Ordinal(args[1], name, name is "stride" or "step");
            if (name == "take" && count == 0) return new RangeValue(Array.Empty<Value>(), source.ItemType);
            return Adapt(source, source.ItemType, (range, cancellation) => CountedRange(range, name, count, cancellation), token);
        }
        if (name is "reverse" or "unique")
            return Adapt(source, source.ItemType, (range, cancellation) => OrderedRange(range, name, cancellation), token);
        return await ConsumeRangeAsync(source, name, args, invoke, token);
    }

    private static RangeValue Adapt(RangeValue source, TypeRef itemType,
        Func<RangeValue, CancellationToken, IAsyncEnumerable<Value>> operation, CancellationToken token)
    {
        PathSelection? selection = source.Selection;
        PathSelection? adapted = selection is null ? null : selection with
        {
            Apply = (items, cancellation) => operation(new RangeValue(
                selection.Apply is null ? items : selection.Apply(items, cancellation), source.ItemType), cancellation)
        };
        return new RangeValue(operation(source, token), itemType, adapted,
            source.Resolve is null ? null : async cancellation => Adapt(await source.Resolve(cancellation), itemType, operation, cancellation));
    }

    private static async IAsyncEnumerable<Value> CountedRange(RangeValue source, string operation, long count,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (operation == "take" && count == 0) yield break;
        long remaining = count;
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            if (operation == "take")
            {
                yield return value;
                if (--remaining == 0) yield break;
                continue;
            }
            if (operation == "drop")
            {
                if (remaining == 0) yield return value;
                else --remaining;
                continue;
            }
            if (remaining == count) yield return value;
            if (--remaining == 0) remaining = count;
        }
    }

    private static async IAsyncEnumerable<Value> CallbackRange(RangeValue source, string operation, Value callback,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke,
        [EnumeratorCancellation] CancellationToken token)
    {
        bool dropping = operation == "drop_while";
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            if (operation == "drop_while" && !dropping) { yield return value; continue; }
            Value result = await invoke(callback, [value], token);
            if (operation == "transform") { yield return result; continue; }
            bool selected = Boolean(result);
            if (operation == "take_while" && !selected) yield break;
            if (operation == "drop_while") { dropping = selected; if (!selected) yield return value; continue; }
            if (selected) yield return value;
        }
    }

    private static async IAsyncEnumerable<Value> OrderedRange(RangeValue source, string operation,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (operation == "unique")
        {
            HashSet<Value> seen = [];
            await foreach (Value value in source.Items.WithCancellation(token)) if (seen.Add(value)) yield return value;
            yield break;
        }
        List<Value> values = [];
        await foreach (Value value in source.Items.WithCancellation(token)) values.Add(value);
        for (int index = values.Count - 1; index >= 0; --index) yield return values[index];
    }

    private static async ValueTask<Value> ConsumeRangeAsync(RangeValue source, string operation, IReadOnlyList<Value> args,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken token)
    {
        if (operation is ("front" or "first") && source.OwnedSnapshot is { } snapshot)
            return snapshot.Count != 0 ? snapshot[0]
                : throw new MsyncException($"ranges.{operation} found no matching item");
        Value? last = null;
        await foreach (Value value in source.Items.WithCancellation(token))
        {
            if (operation == "empty") return Value.False;
            if (operation is "front" or "first") return value;
            if (operation is "back" or "last") { last = value; continue; }
            bool matches = operation is "contains" or "find" ? value == args[1] : Boolean(await invoke(args[1], [value], token));
            if (operation is "contains" && matches) return Value.True;
            if (operation is "find" or "find_if" && matches) return value;
            if (operation is "any" or "any_of" && matches) return Value.True;
            if (operation == "all_of" && !matches) return Value.False;
            if (operation is "none" or "none_of" && matches) return Value.False;
        }
        return operation switch
        {
            "empty" or "all_of" or "none" or "none_of" => Value.True,
            "contains" or "any" or "any_of" => Value.False,
            "back" or "last" => last ?? throw new MsyncException($"ranges.{operation} requires a nonempty Range"),
            "front" or "first" or "find" or "find_if" => throw new MsyncException($"ranges.{operation} found no matching item"),
            _ => throw new MsyncException($"Unknown range native '{operation}'")
        };
    }
}
