using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private async ValueTask<NativeDatum> InvokeNativeDatumAsync(string name, IReadOnlyList<NativeDatum> arguments,
        NativeRuntimeContext origin, SourceSpan? source, CancellationToken token, NativeType? rangePhysicalType = null, Msync.Core.NativeSignature? signature = null)
    {
        List<NativeFuture>? dependencies = null;
        foreach (NativeDatum argument in arguments)
            if (argument.Future is { } dependency) (dependencies ??= []).Add(dependency);
        if (signature is null) NativeSignatures.TryGet(name, out signature);
        if (signature is not null && await TryRangeDatumAsync(signature, arguments, token, rangePhysicalType) is { } rangeResult) return rangeResult;
        bool scalarView = signature is not null && (signature.Behavior.GetValueOrDefault() & NativeBehavior.DeferredScalar) != 0;
        if ((dependencies is not null || scalarView) && DefersScalarNative(signature, arguments))
        {
            List<NativeProcess>? processes = null;
            foreach (NativeDatum argument in arguments)
                if (argument.IsReady && argument.Value.Kind == NativeKind.Process)
                    (processes ??= []).Add(argument.Value.AsProcess());
            return Track(new(new NativeFuture(InvokeDeferredNativeAsync(name, arguments, origin, token), dependencies,
                source: source, processes: processes, logicalType: signature!.Result)));
        }
        return new(await InvokeNativeAsync(name, await ResolveArguments(arguments, token), origin, token));
    }

    private static bool DefersScalarNative(Msync.Core.NativeSignature? signature, IReadOnlyList<NativeDatum> arguments)
    {
        if (signature is null || (signature.Effect != NativeEffect.Pure
                && (signature.Behavior.GetValueOrDefault() & NativeBehavior.DeferredScalar) == 0)
            || arguments.Count > signature.Parameters.Count
            || (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.LocalMemory)) != 0)
            return false;
        const TypeKind mutableOrInvoked = TypeKind.Range | TypeKind.Function | TypeKind.Scope;
        return signature.Parameters.All(static parameter => (parameter.Kind & mutableOrInvoked) == 0)
            && arguments.All(static argument => !argument.IsReady || argument.Value.Kind is not (NativeKind.Range or NativeKind.Function or NativeKind.Address));
    }

    private async Task<NativeValue> InvokeDeferredNativeAsync(string name, IReadOnlyList<NativeDatum> arguments,
        NativeRuntimeContext origin, CancellationToken token) =>
        await InvokeNativeAsync(name, await ResolveArguments(arguments, token), origin, token);

}
