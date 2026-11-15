using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly partial struct NativeDatum
{
    internal static NativeDatum Select(NativeDatum condition, NativeDatum whenTrue, NativeDatum whenFalse,
        SourceSpan? source, TypeRef? logicalType, NativeType physicalType)
    {
        if (condition.IsReady) return condition.Value.AsBool() ? whenTrue : whenFalse;
        // Dependencies retain candidate resources across the submitting frame's exit.
        // Retention does not demand either value; the completion below selects first.
        List<NativeFuture> dependencies = [condition.Future!];
        if (whenTrue.Future is { } first) dependencies.Add(first);
        if (whenFalse.Future is { } second) dependencies.Add(second);
        return new(new NativeFuture(SelectAsync(condition, whenTrue, whenFalse), dependencies,
            source: source, logicalType: logicalType, physicalType: physicalType));
    }

    private static async Task<NativeValue> SelectAsync(NativeDatum condition, NativeDatum whenTrue, NativeDatum whenFalse)
    {
        bool selected = (await condition.ResolveAsync(CancellationToken.None).ConfigureAwait(false)).AsBool();
        return await (selected ? whenTrue : whenFalse).ResolveAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
