using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly partial struct NativeDatum
{
    internal NativeDatum MapMachine<TState>(TState state, Func<NativeValue, TState, NativeValue> transform,
        NativeType physicalType, SourceSpan? source = null)
    {
        if (IsReady) return new(transform(value, state));
        NativeFuture dependency = Future!;
        return new(new NativeFuture(ApplyMachineAsync(dependency, state, transform), [dependency], source: source,
            logicalType: MachineResultSourceType(physicalType), physicalType: physicalType));
    }

    internal static NativeDatum CombineMachine<TState>(NativeDatum left, NativeDatum right, TState state,
        Func<NativeValue, NativeValue, TState, NativeValue> transform, NativeType physicalType, SourceSpan? source = null)
    {
        if (left.IsReady && right.IsReady) return new(transform(left.value, right.value, state));
        NativeFuture[] dependencies = left.Future is { } first
            ? right.Future is { } second ? [first, second] : [first] : [right.Future!];
        return new(new NativeFuture(CombineMachineAsync(left, right, state, transform), dependencies, source: source,
            logicalType: MachineResultSourceType(physicalType), physicalType: physicalType));
    }

    private static TypeRef? MachineResultSourceType(NativeType type) => type.OptionalSourceType;
    private static async Task<NativeValue> ApplyMachineAsync<TState>(NativeFuture input, TState state,
        Func<NativeValue, TState, NativeValue> transform) =>
        transform(await input.CompleteAsync(CancellationToken.None).ConfigureAwait(false), state);
    private static async Task<NativeValue> CombineMachineAsync<TState>(NativeDatum left, NativeDatum right, TState state,
        Func<NativeValue, NativeValue, TState, NativeValue> transform) =>
        transform(await left.ResolveAsync(CancellationToken.None).ConfigureAwait(false),
            await right.ResolveAsync(CancellationToken.None).ConfigureAwait(false), state);
}
