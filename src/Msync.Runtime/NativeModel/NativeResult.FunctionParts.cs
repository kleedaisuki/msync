using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly partial struct NativeDatum
{
    internal static NativeDatum ComposeFunction(NativeDatum code, NativeDatum environment, NativeDatum name,
        SourceSpan? source, Func<NativeAddress, NativeEnvironmentLayout> environmentLayout) =>
        ComposeFunction(code, environment, name, [], source, environmentLayout);

    internal static NativeDatum ComposeFunction(NativeDatum code, NativeDatum environment, NativeDatum name,
        NativeEnvironmentLayout[] layouts, SourceSpan? source,
        Func<NativeAddress, NativeEnvironmentLayout>? environmentLayout = null)
    {
        if (code.IsReady && environment.IsReady && name.IsReady)
            return new(NativeFunctionParts.Compose(code.Value, environment.Value, name.Value, layouts, environmentLayout));
        List<NativeFuture> dependencies = [];
        if (code.Future is { } codeFuture) dependencies.Add(codeFuture);
        if (environment.Future is { } envFuture) dependencies.Add(envFuture);
        if (name.Future is { } nameFuture) dependencies.Add(nameFuture);
        return new(new NativeFuture(ComposeFunctionAsync(code, environment, name, layouts, environmentLayout), dependencies,
            source: source, logicalType: new TypeRef(TypeKind.Function), physicalType: NativeType.Of(NativeKind.Function)));
    }

    private static async Task<NativeValue> ComposeFunctionAsync(NativeDatum code, NativeDatum environment,
        NativeDatum name, NativeEnvironmentLayout[] layouts, Func<NativeAddress, NativeEnvironmentLayout>? environmentLayout)
    {
        NativeValue codeValue = await code.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
        NativeValue envValue = await environment.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
        NativeValue nameValue = await name.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
        return NativeFunctionParts.Compose(codeValue, envValue, nameValue, layouts, environmentLayout);
    }
}
