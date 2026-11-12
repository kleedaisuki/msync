namespace Msync.Core;

public sealed record RangeValue(IAsyncEnumerable<Value> Items, TypeRef ItemType, PathSelection? Selection = null,
    Func<CancellationToken, ValueTask<RangeValue>>? Resolve = null) : Value
{
    public bool Equals(RangeValue? other) => ReferenceEquals(this, other) || other is not null
        && EqualityComparer<IAsyncEnumerable<Value>>.Default.Equals(Items, other.Items)
        && ItemType == other.ItemType && Selection == other.Selection && Resolve == other.Resolve;
    public override int GetHashCode() => HashCode.Combine(typeof(RangeValue), Items, ItemType, Selection, Resolve);

    // Roots already retained by storage or a lazy producer; ownership never
    // enumerates a Range or keeps discarded snapshot inputs alive.
    public IReadOnlyList<Value>? RetainedValues { get; set; }
    public IReadOnlyList<Value>? SnapshotValues { get; private init; }
    private readonly IAsyncEnumerable<Value>? snapshotProducer;
    public RangeValue(IEnumerable<Value> items, TypeRef itemType) : this(Enumerate(items), itemType)
    {
        RetainedValues = SnapshotValues = items as IReadOnlyList<Value>;
        if (items is Value[]) snapshotProducer = Items;
    }

    private RangeValue(List<Value> items, TypeRef itemType) : this((IEnumerable<Value>)items, itemType)
        => snapshotProducer = Items;

    private RangeValue(SingletonSnapshot item) : this((IAsyncEnumerable<Value>)item, item.Value.Type)
    {
        RetainedValues = SnapshotValues = item;
        snapshotProducer = item;
    }

    internal static RangeValue NumberSequence(double start, double stop, double step, CancellationToken producerToken) =>
        new(new NumberRangeItems.Sequence(start, stop, step, producerToken), TypeRef.Number);

    internal static RangeValue OwnNumberSnapshot(List<double> items, TypeRef itemType) =>
        OwnScalarSnapshot(new NumberRangeItems.Snapshot(items), itemType);

    internal static RangeValue CopyNumberSnapshot(NumberRangeItems.Snapshot items, TypeRef itemType) =>
        OwnScalarSnapshot(items.CopyView(), itemType);

    internal static RangeValue TextSlices(IEnumerable<TextSlice> items) =>
        new(StringRangeItems.From(items), TypeRef.String);

    internal static RangeValue OwnTextSnapshot(List<string> items, TypeRef itemType) =>
        OwnScalarSnapshot(new StringRangeItems.Snapshot(items), itemType);

    internal static RangeValue CopyTextSnapshot(StringRangeItems.Snapshot items, TypeRef itemType) =>
        OwnScalarSnapshot(items.CopyView(), itemType);

    private RangeValue(IAsyncEnumerable<Value> producer, IReadOnlyList<Value> snapshot, TypeRef itemType) : this(producer, itemType)
    {
        SnapshotValues = snapshot;
        snapshotProducer = producer;
    }

    private static RangeValue OwnScalarSnapshot<T>(T items, TypeRef itemType)
        where T : IAsyncEnumerable<Value>, IReadOnlyList<Value> => new(items, items, itemType);

    internal static RangeValue OwnSingleton(Value value) => new(new SingletonSnapshot(value));

    /// <summary>Publish a private list that the caller will no longer mutate.</summary>
    public static RangeValue TakeSnapshotOwnership(List<Value> items, TypeRef itemType) => new(items, itemType);

    public IReadOnlyList<Value>? OwnedSnapshot =>
        ReferenceEquals(Items, snapshotProducer) ? SnapshotValues : null;
    private static async IAsyncEnumerable<Value> Enumerate(IEnumerable<Value> items)
    {
        foreach (var item in items) yield return item;
        await Task.CompletedTask;
    }
    public override TypeRef Type => new(TypeKind.Range, ItemType);
    public override string ToString() => $"Range<{ItemType}>";

    private sealed class SingletonSnapshot(Value value) : IAsyncEnumerable<Value>, IReadOnlyList<Value>
    {
        internal Value Value => value;
        public int Count => 1;
        public Value this[int index] => index == 0 ? value : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<Value> GetEnumerator()
        {
            yield return value;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerator<Value> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            yield return value;
            await Task.CompletedTask;
        }
    }
}
