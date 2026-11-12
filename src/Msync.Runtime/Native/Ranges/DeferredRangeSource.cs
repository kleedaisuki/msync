using Msync.Core;

namespace Msync.Runtime;

/// <summary>A transparent, memoized producer call, evaluated on first demand.</summary>
internal sealed class DeferredRangeSource(Func<CancellationToken, Task<RangeValue>> evaluate) : IAsyncEnumerable<Value>
{
    private Task<RangeValue>? resolved;

    internal ValueTask<RangeValue> Resolve(CancellationToken token) => new(resolved ??= evaluate(token));

    internal static ValueTask<RangeValue> Demand(RangeValue range, CancellationToken token)
    {
        while (range.Items is DeferredRangeSource source)
        {
            var pending = source.Resolve(token);
            if (!pending.IsCompletedSuccessfully) return Resume(pending, token);
            range = pending.Result;
        }
        return new(range);

        static async ValueTask<RangeValue> Resume(ValueTask<RangeValue> pending, CancellationToken token) =>
            await Demand(await pending, token);
    }

    public async IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var range = await Demand(await Resolve(cancellationToken), cancellationToken);
        await foreach (Value item in range.Items.WithCancellation(cancellationToken)) yield return item;
    }
}
