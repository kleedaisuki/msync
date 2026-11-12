using System.Collections;

namespace Msync.Core;

internal readonly record struct TextSlice(string Text, int Offset, int Length)
{
    internal TextSlice(string text) : this(text, 0, text.Length) { }
    internal ReadOnlySpan<char> Span => Text.AsSpan(Offset, Length);
    internal string ToOwnedString() => Text.Substring(Offset, Length);
}

/// <summary>A Range of managed String references or slices, boxed only by the Value projection.</summary>
internal abstract class StringRangeItems : IAsyncEnumerable<Value>
{
    internal static StringRangeItems From(IEnumerable<TextSlice> slices) => new Source(slices);

    public abstract IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default);

    internal abstract class Sync : StringRangeItems
    {
        internal abstract IEnumerable<TextSlice> Read();
        public sealed override IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new BoxedSync(Read().GetEnumerator(), cancellationToken);
    }

    internal sealed class Async(IAsyncEnumerable<TextSlice> stream) : StringRangeItems
    {
        internal IAsyncEnumerable<TextSlice> Read => stream;
        public override IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new BoxedAsync(stream.GetAsyncEnumerator(cancellationToken));
    }

    // Generic consumers still see Values. Delegate movement/disposal instead
    // of adding an async iterator state machine around the selected source.
    private sealed class BoxedAsync(IAsyncEnumerator<TextSlice> source) : IAsyncEnumerator<Value>
    {
        private StringValue? current;
        public Value Current => current ??= new StringValue(source.Current.ToOwnedString());
        public ValueTask<bool> MoveNextAsync()
        {
            current = null;
            return source.MoveNextAsync();
        }
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    private sealed class BoxedSync(IEnumerator<TextSlice> source, CancellationToken token) : IAsyncEnumerator<Value>
    {
        private StringValue? current;
        public Value Current => current ??= new StringValue(source.Current.ToOwnedString());
        public ValueTask<bool> MoveNextAsync()
        {
            current = null;
            bool available = source.MoveNext();
            if (available) token.ThrowIfCancellationRequested();
            return new(available);
        }
        public ValueTask DisposeAsync()
        {
            source.Dispose();
            return default;
        }
    }

    private sealed class Source(IEnumerable<TextSlice> slices) : Sync
    {
        internal override IEnumerable<TextSlice> Read() => slices;
    }

    internal sealed class Snapshot(List<string> items) : Sync, IReadOnlyList<Value>
    {
        public int Count => items.Count;
        public Value this[int index] => new StringValue(items[index]);
        internal Snapshot CopyView() => new(items);

        internal override IEnumerable<TextSlice> Read()
        {
            foreach (string item in items) yield return new TextSlice(item);
        }

        public IEnumerator<Value> GetEnumerator()
        {
            foreach (string item in items) yield return new StringValue(item);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
