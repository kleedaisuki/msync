using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private sealed record IteratorValue : Value
    {
        internal RangeValue? Pending;
        internal IAsyncEnumerator<Value>? Iterator;
        internal NumberCursor Numbers;
        public override TypeRef Type => TypeRef.Any;
    }

    private IteratorValue OpenIterator(RangeValue range, CancellationToken token)
    {
        var cursor = new IteratorValue();
        if (range.Items is DeferredRangeSource) cursor.Pending = range;
        else BindIterator(cursor, range, token);
        return cursor;
    }

    private void BindIterator(IteratorValue cursor, RangeValue range, CancellationToken token)
    {
        cursor.Pending = null;
        if (range.Items is NumberRangeItems numbers) cursor.Numbers = numbers.Open(token);
        else iterators.Add(cursor.Iterator = range.Items.GetAsyncEnumerator(token));
    }

    private async ValueTask<Value> CloseIteratorAsync(Value value)
    {
        if (value is not IteratorValue cursor) throw new MsyncException("Loop cleanup requires an iterator");
        if (cursor.Iterator is not { } iterator || !iterators.Contains(iterator)) return Value.Null;
        await iterator.DisposeAsync();
        iterators.Remove(iterator);
        return Value.Null;
    }
    private IAsyncEnumerable<Value> LazyItemsAsync(IReadOnlyList<Value> values, string? mask, CancellationToken cancellationToken)
    {
        // A REPL thunk may observe functions projected by a later submission.
        if (sessionImage is null && (mask is null || mask.Length == values.Count))
        {
            Module image = module;
            var plan = PlanLiteralScopes(image, values, mask, out bool allLiteral);
            if (allLiteral)
            {
                var rows = plan ?? [];
                return new LiteralScopeItems(this, image, rows, cancellationToken,
                    EnumeratePlannedItemsAsync(image, values, mask, rows, cancellationToken));
            }
            if (plan is not null)
                return EnumeratePlannedItemsAsync(image, values, mask, plan, cancellationToken);
        }
        return EnumerateLazyItemsAsync(values, mask, cancellationToken);
    }

    private async IAsyncEnumerable<Value> EnumerateLazyItemsAsync(IReadOnlyList<Value> values, string? mask,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < values.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return mask is null || mask[i] == '1' ? await InvokeAsync(values[i], [], cancellationToken) : values[i];
        }
    }

    private async IAsyncEnumerable<Value> EnumeratePlannedItemsAsync(Module image, IReadOnlyList<Value> values,
        string? mask, LiteralScopeProducer?[] plan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < values.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan[i] is { } producer) yield return PullLiteralScope(image, producer);
            else yield return mask is null || mask[i] == '1' ? await InvokeAsync(values[i], [], cancellationToken) : values[i];
        }
    }
    private RangeValue CreateDeferredRange(Value thunk)
    {
        var itemType = thunk is FunctionValue { NativeName: null } function
            ? module.Functions[function.Function].ReturnType.Element ?? TypeRef.Any : TypeRef.Any;
        async Task<RangeValue> Evaluate(CancellationToken cancellationToken) => await InvokeAsync(thunk, [], cancellationToken) as RangeValue
            ?? throw new MsyncException("Deferred range function did not return Range");
        var source = new DeferredRangeSource(Evaluate);
        return new RangeValue(source, itemType, Resolve: source.Resolve)
        { RetainedValues = (thunk as FunctionValue)?.Captures };
    }
}
