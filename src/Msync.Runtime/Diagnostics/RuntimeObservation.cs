namespace Msync.Runtime;

public sealed record RuntimeObservation(string Phase, string Outcome, string? Source = null, string? Destination = null, string? Detail = null, long DurationNanoseconds = 0);
