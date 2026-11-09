namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterTextBuilder(IDictionary<string, NativeSignature> result)
    {
        TypeRef chunks = new(TypeKind.Range, TypeRef.String);
        result.Add("text.append_chunk", new("text.append_chunk", [chunks, TypeRef.String], chunks,
            Behavior: NativeBehavior.EagerRange | NativeBehavior.Enumerates | NativeBehavior.MayFail));
    }
}
