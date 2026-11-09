namespace Msync.Runtime;

public readonly record struct SchedulingStatistics(long AdmittedTransfers, long ResourceNodesVisited, long DependencyEdges, long NativeHazardWaits);
