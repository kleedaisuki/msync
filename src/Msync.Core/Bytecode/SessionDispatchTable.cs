namespace Msync.Core;

// Session generations publish complete tables; earlier deferred values retain
// the slot, not the set of callable definitions known when they were created.
public sealed record SessionDispatchTable(int Slot, IReadOnlyDictionary<int, int> Targets);
