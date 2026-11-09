using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private async ValueTask<Value> CloseIteratorAsync(Value value)
    {
        if (value is not IteratorValue iterator) throw new MsyncException("Loop cleanup requires an iterator");
        if (!iterators.Contains(iterator.Iterator)) return Value.Null;
        await iterator.Iterator.DisposeAsync();
        iterators.Remove(iterator.Iterator);
        return Value.Null;
    }
    private async IAsyncEnumerable<Value> LazyItemsAsync(IReadOnlyList<Value> values, string? mask, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < values.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return mask is null || mask[i] == '1' ? await InvokeAsync(values[i], [], cancellationToken) : values[i];
        }
    }
    private RangeValue CreateDeferredRange(Value thunk)
    {
        var itemType = thunk is FunctionValue { NativeName: null } function
            ? module.Functions[function.Function].ReturnType.Element ?? TypeRef.Any : TypeRef.Any;
        Task<RangeValue>? resolved = null;
        async Task<RangeValue> Evaluate(CancellationToken cancellationToken) => await InvokeAsync(thunk, [], cancellationToken) as RangeValue
            ?? throw new MsyncException("Deferred range function did not return Range");
        ValueTask<RangeValue> Resolve(CancellationToken cancellationToken) => new(resolved ??= Evaluate(cancellationToken));
        return new RangeValue(EnumerateDeferredRangeAsync(Resolve), itemType, Resolve: Resolve)
        { RetainedValues = (thunk as FunctionValue)?.Captures };
    }
    private static async IAsyncEnumerable<Value> EnumerateDeferredRangeAsync(Func<CancellationToken, ValueTask<RangeValue>> resolve,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var range = await resolve(cancellationToken);
        await foreach (var value in range.Items.WithCancellation(cancellationToken)) yield return value;
    }
}
