namespace Msync.Core;

public interface IRangeTypeOrigin
{
    TypeRef InitialItemType { get; }
}

public sealed record RangeValue(IAsyncEnumerable<Value> Items, TypeRef ItemType, PathSelection? Selection = null,
    Func<CancellationToken, ValueTask<RangeValue>>? Resolve = null) : Value
{
    private TypeRef itemType = ItemType;
    public TypeRef ItemType { get => itemType; init => itemType = value; }

    public void InferItemType(Value value)
    {
        if (itemType.Kind == TypeKind.Never) itemType = value.Type;
    }

    // A first-write carrier solution must not move a live Range between hash buckets.
    private TypeRef IdentityItemType => Items is IRangeTypeOrigin origin ? origin.InitialItemType : ItemType;
    public bool Equals(RangeValue? other) => ReferenceEquals(this, other) || other is not null
        && EqualityComparer<IAsyncEnumerable<Value>>.Default.Equals(Items, other.Items)
        && IdentityItemType == other.IdentityItemType && Selection == other.Selection && Resolve == other.Resolve;
    public override int GetHashCode() => HashCode.Combine(typeof(RangeValue), Items, IdentityItemType, Selection, Resolve);

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
}
