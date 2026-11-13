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

public sealed record ExecutionBatch(IReadOnlyList<int> Transfers, string Endpoint);
