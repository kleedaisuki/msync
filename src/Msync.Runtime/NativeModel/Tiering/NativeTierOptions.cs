namespace Msync.Runtime.Native;

public enum NativeJitPolicy : byte { Off, Auto, Force, Linked }

public sealed record NativeTierOptions(NativeJitPolicy Policy = NativeJitPolicy.Auto,
    int CallThreshold = 32, int BackedgeThreshold = 128, int RecompileThreshold = 64)
{
    internal static NativeTierOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("MSYNC_JIT")?.ToLowerInvariant() switch
        {
            "off" or "0" or "false" => NativeJitPolicy.Off,
            "force" => NativeJitPolicy.Force,
            "linked" => NativeJitPolicy.Linked,
            _ => NativeJitPolicy.Auto
        });

    internal void Validate()
    {
        if (!Enum.IsDefined(Policy)) throw new ArgumentOutOfRangeException(nameof(Policy));
        ArgumentOutOfRangeException.ThrowIfLessThan(CallThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(BackedgeThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(RecompileThreshold, 1);
    }
}

public readonly record struct NativeTierStatistics(long Compilations, long CompilationNanoseconds,
    long CodeBytes, long Entries, long Operations, long DirectOperations, long GuardMisses,
    long Deoptimizations, long Suspensions, long Recompilations, long CancellationPolls,
    long UnavailableCompilations)
{
    public long LinkedEntries { get; init; }
    public long LinkedOperations { get; init; }
    public long RawOperations { get; init; }
}
