namespace Msync.Core;

public enum PlannedTransferKind : byte
{
    GenericCopy,
    CopyFile,
    CopyDirectory,
    CopyLink,
    WriteText,
    EnsureDirectory,
    ApplyDirectoryMetadata
}

public enum TransferDestinationMode : byte
{
    Exact,
    Merge,
    Replace
}

public sealed record PlanResource(string Endpoint, string Path, bool Subtree = false);

public sealed record PlannedTransfer(int Id, Value Source, PathValue Destination,
    PlannedTransferKind Kind, IReadOnlyList<PlanResource> Reads, IReadOnlyList<PlanResource> Writes,
    IReadOnlyList<int> Predecessors, SourceSpan? Span = null,
    TransferDestinationMode Mode = TransferDestinationMode.Exact);

public sealed record ExecutionBatch(IReadOnlyList<int> Transfers, string Endpoint);

public sealed record ExecutionPlan(int Function, int Start, int End,
    IReadOnlyList<PlannedTransfer> Transfers, IReadOnlyList<ExecutionBatch> Batches,
    int EliminatedTransfers, IReadOnlyList<int>? ResultTransfers = null);
