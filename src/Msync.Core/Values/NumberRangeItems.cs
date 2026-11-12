using System.Collections;

namespace Msync.Core;

/// <summary>A synchronous Range producer with a primitive numeric payload.</summary>
internal abstract class NumberRangeItems : IAsyncEnumerable<Value>
{
    internal abstract NumberCursor Open(CancellationToken cancellationToken);

    public async IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var cursor = Open(cancellationToken);
        while (cursor.TryNext(out double value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new NumberValue(value);
        }
        await Task.CompletedTask;
    }

    internal sealed class Sequence(double start, double stop, double step, CancellationToken producerToken) : NumberRangeItems
    {
        internal override NumberCursor Open(CancellationToken cancellationToken) =>
            new(start, stop, step, producerToken == cancellationToken ? default : producerToken);
    }

    internal sealed class Snapshot(List<double> values) : NumberRangeItems, IReadOnlyList<Value>
    {
        public int Count => values.Count;
        public Value this[int index] => new NumberValue(values[index]);
        internal override NumberCursor Open(CancellationToken cancellationToken) => new(values);
        internal Snapshot CopyView() => new(values);

        public IEnumerator<Value> GetEnumerator()
        {
            foreach (double value in values) yield return new NumberValue(value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// The descriptor selects one homogeneous carrier; individual Numbers have
/// no discriminator. A cursor cannot suspend or retain external resources.
/// </summary>
internal struct NumberCursor
{
    private readonly List<double>? snapshot;
    private readonly double stop;
    private readonly double step;
    private readonly CancellationToken producerToken;
    private double current;
    private int position;

    internal NumberCursor(List<double> values) => snapshot = values;
    internal NumberCursor(double start, double stop, double step, CancellationToken producerToken) =>
        (current, this.stop, this.step, this.producerToken) = (start, stop, step, producerToken);

    internal bool TryNext(out double value)
    {
        if (snapshot is { } values)
        {
            if (position < values.Count)
            {
                value = values[position++];
                return true;
            }
            value = default;
            return false;
        }

        if (position == 1)
        {
            double next = current + step;
            if (next == current) throw new MsyncException("ranges.sequence step no longer advances the Number value");
            current = next;
        }
        value = current;
        bool hasNext = step > 0 ? value < stop : value > stop;
        position = hasNext ? 1 : 0;
        if (hasNext) producerToken.ThrowIfCancellationRequested();
        return hasNext;
    }
}
