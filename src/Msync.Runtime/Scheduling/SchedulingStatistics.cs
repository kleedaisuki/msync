namespace Msync.Runtime;

public readonly record struct SchedulingStatistics(long AdmittedTransfers, long ResourceNodesVisited, long DependencyEdges, long NativeHazardWaits)
{
    public long SubmittedPlans { get; init; }
    public long SubmittedBatches { get; init; }
    public long AdmittedOperations { get; init; }
    public long EliminatedTransfers { get; init; }
}
