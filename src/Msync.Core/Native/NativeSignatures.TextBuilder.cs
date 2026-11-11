namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterTextBuilder(IDictionary<string, NativeSignature> result)
    {
        TypeRef chunks = new(TypeKind.Range, TypeRef.String);
        result.Add("text.append_chunk", new("text.append_chunk", [chunks, TypeRef.String], chunks,
            Behavior: NativeBehavior.EagerRange | NativeBehavior.Enumerates | NativeBehavior.MayFail));
        result.Add("text.format", new("text.format", [TypeRef.String], TypeRef.String,
            Variadic: true, Behavior: NativeBehavior.MayFail));
        result.Add("text.format_fields", new("text.format_fields", [TypeRef.String, TypeRef.Scope], TypeRef.String,
            NativeEffect.Read, Behavior: NativeBehavior.LocalMemory | NativeBehavior.MayFail));
    }
}
