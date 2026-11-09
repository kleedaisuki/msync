namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterIteration(Dictionary<string, NativeSignature> signatures) =>
        signatures.Add("_core.iterator_close", new("_core.iterator_close", [TypeRef.Any], TypeRef.Null,
            NativeEffect.Write, Behavior: NativeBehavior.Invokes | NativeBehavior.MayFail));
}
