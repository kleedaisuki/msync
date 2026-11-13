namespace Msync.Core.Native;

/// <summary>Portable selected transfer input, never a live value or resource.</summary>
public sealed class NativePlanValue
{
    private NativePlanValue(NativeKind kind, NativeConstant? literal = null,
        NativePath? path = null, IReadOnlyList<NativePlanValue>? items = null,
        TypeRef? itemType = null) =>
        (Kind, Literal, Path, Items, ItemType) = (kind, literal, path, items, itemType);

    public NativeKind Kind { get; }
    public NativeConstant? Literal { get; }
    public NativePath? Path { get; }
    public IReadOnlyList<NativePlanValue>? Items { get; }
    public TypeRef? ItemType { get; }

    public static NativePlanValue Scalar(NativeConstant literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        if (literal.Kind is not (NativeKind.Null or NativeKind.Bool or NativeKind.Number
            or NativeKind.String or NativeKind.Connection or NativeKind.Type))
            throw new ArgumentException("A plan scalar must contain portable literal data.", nameof(literal));
        return new(literal.Kind, literal: literal);
    }

    public static NativePlanValue FromPath(NativePath path) => new(NativeKind.Path, path: path);

    public static NativePlanValue Snapshot(IEnumerable<NativePlanValue> items, TypeRef itemType)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemType);
        return new(NativeKind.Range, items: Array.AsReadOnly(items.ToArray()), itemType: itemType);
    }
}

public sealed record NativePlannedTransfer(int Id, NativePlanValue Source, NativePath Destination,
    PlannedTransferKind Kind, IReadOnlyList<PlanResource> Reads, IReadOnlyList<PlanResource> Writes,
    IReadOnlyList<int> Predecessors, SourceSpan? Span = null,
    TransferDestinationMode Mode = TransferDestinationMode.Exact);

public sealed record NativeExecutionPlan(int Function, int Start, int End,
    IReadOnlyList<NativePlannedTransfer> Transfers, IReadOnlyList<ExecutionBatch> Batches,
    int EliminatedTransfers, IReadOnlyList<int>? ResultTransfers = null);
